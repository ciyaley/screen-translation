#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME_DIR="${SCREEN_TRANSLATE_RUNTIME_DIR:-${ROOT_DIR}/.runtime}"
mkdir -p "${RUNTIME_DIR}"

BREW_ARM="/""opt/homebrew/bin"
BREW_INTEL="/""usr/local/bin"
export PATH="${BREW_ARM}:${BREW_INTEL}:${HOME}/.local/bin:${HOME}/.cargo/bin:${PATH}"

PORT="${SCREEN_TRANSLATE_PLAMO_PORT:-18492}"
PID_FILE="${RUNTIME_DIR}/plamo_server.pid"
SIZE_FILE="${RUNTIME_DIR}/plamo_active_size"
STATUS_FILE="${RUNTIME_DIR}/plamo_status.json"
LOG_FILE="${RUNTIME_DIR}/plamo_server.log"
LOCK_DIR="${RUNTIME_DIR}/plamo_launch.lock"
NC_BIN="/""usr/bin/nc"

CMD="${1:-start}"
REQ_SIZE="${2:-2b}"

# python3 を一切起動せず純粋なシェル内蔵処理のみでステータスJSONをアトミック更新（CPU負荷ゼロ）
write_status() {
    local state="$1"
    local size="$2"
    local backend="$3"
    local msg="${4//\"/\'}"
    local tmp="${STATUS_FILE}.tmp.$$"
    printf '{"state":"%s","size":"%s","backend":"%s","message":"%s"}\n' \
        "${state}" "${size}" "${backend}" "${msg}" > "${tmp}"
    mv -f "${tmp}" "${STATUS_FILE}"
}

# Cバイナリ nc による軽量ソケット疎通確認（サブプロセスPython起動オーバーヘッドを完全排除）
is_port_ready() {
    "${NC_BIN}" -z -G 1 127.0.0.1 "${PORT}" >/dev/null 2>&1
}

stop_plamo() {
    if [[ -f "${PID_FILE}" ]]; then
        local old_pid
        old_pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
        if [[ -n "${old_pid}" ]] && kill -0 "${old_pid}" 2>/dev/null; then
            kill -TERM "${old_pid}" 2>/dev/null || true
            for _ in {1..15}; do
                kill -0 "${old_pid}" 2>/dev/null || break
                sleep 0.1
            done
            kill -9 "${old_pid}" 2>/dev/null || true
        fi
        rm -f "${PID_FILE}"
    fi
    rm -f "${SIZE_FILE}"
}

release_lock() {
    rm -f "${LOCK_DIR}/owner.pid" 2>/dev/null || true
    rmdir "${LOCK_DIR}" 2>/dev/null || true
}

acquire_lock_or_exit() {
    if mkdir "${LOCK_DIR}" 2>/dev/null; then
        return 0
    fi
    if [[ -f "${LOCK_DIR}/owner.pid" ]]; then
        local lock_pid
        lock_pid="$(cat "${LOCK_DIR}/owner.pid" 2>/dev/null || true)"
        if [[ -n "${lock_pid}" ]] && kill -0 "${lock_pid}" 2>/dev/null; then
            # 既に別の起動ワーカーが正常進行中なので二重起動せず即終了
            exit 0
        fi
    fi
    rm -rf "${LOCK_DIR}" 2>/dev/null || true
    mkdir "${LOCK_DIR}" 2>/dev/null || exit 0
}

if [[ "${CMD}" == "stop" ]]; then
    rm -rf "${LOCK_DIR}" 2>/dev/null || true
    stop_plamo
    write_status "stopped" "" "" "停止済み"
    exit 0
fi

case "${REQ_SIZE}" in
    0.8b)
        MLX_REPO="pfnet/plamo-3-translate-0.8b-MLX-4bit"
        GGUF_REPO="pfnet/plamo-3-translate-0.8b-GGUF:Q4_K_M"
        SIZE_LABEL="0.8B"
        ;;
    8b)
        MLX_REPO="pfnet/plamo-3-translate-8b-MLX-4bit"
        GGUF_REPO="pfnet/plamo-3-translate-8b-GGUF:Q4_K_M"
        SIZE_LABEL="8B"
        ;;
    *)
        REQ_SIZE="2b"
        MLX_REPO="pfnet/plamo-3-translate-2b-MLX-4bit"
        GGUF_REPO="pfnet/plamo-3-translate-2b-GGUF:Q4_K_M"
        SIZE_LABEL="2B"
        ;;
esac

# 既に同じサイズで稼働中なら即座に完了（CPU 0%）
if is_port_ready; then
    if [[ -f "${SIZE_FILE}" ]] && [[ "$(cat "${SIZE_FILE}" 2>/dev/null || true)" == "${REQ_SIZE}" ]]; then
        if [[ -f "${PID_FILE}" ]]; then
            CUR_PID="$(cat "${PID_FILE}" 2>/dev/null || true)"
            if [[ -n "${CUR_PID}" ]] && kill -0 "${CUR_PID}" 2>/dev/null; then
                exit 0
            fi
        fi
    fi
    # PIDファイル欠損時でもポートを占有していれば lsof で回収または停止する
    if [[ ! -f "${PID_FILE}" ]] && command -v lsof >/dev/null 2>&1; then
        PORT_PIDS="$(lsof -ti "tcp:${PORT}" -sTCP:LISTEN 2>/dev/null || true)"
        if [[ -n "${PORT_PIDS}" ]]; then
            if [[ ! -f "${SIZE_FILE}" ]] || [[ "$(cat "${SIZE_FILE}" 2>/dev/null || true)" == "${REQ_SIZE}" ]]; then
                echo "${PORT_PIDS}" | head -n 1 > "${PID_FILE}"
                echo "${REQ_SIZE}" > "${SIZE_FILE}"
                write_status "ready" "${REQ_SIZE}" "mlx-lm" "PLaMo 3 (${SIZE_LABEL}) 稼働中"
                exit 0
            else
                kill -TERM ${PORT_PIDS} 2>/dev/null || true
                sleep 0.5
            fi
        fi
    fi
fi

acquire_lock_or_exit

(
    echo "${BASHPID:-$$}" > "${LOCK_DIR}/owner.pid"
    trap 'release_lock' EXIT

    # ロック取得後に再度稼働確認
    if [[ -f "${SIZE_FILE}" ]] && [[ "$(cat "${SIZE_FILE}" 2>/dev/null || true)" == "${REQ_SIZE}" ]] && is_port_ready; then
        exit 0
    fi

    stop_plamo

    VENV_DIR="${RUNTIME_DIR}/plamo-mlx-venv"
    MODEL_DIR="${RUNTIME_DIR}/models/plamo-3-translate-${REQ_SIZE}-MLX-4bit"
    mkdir -p "${RUNTIME_DIR}/models"

    # Apple Silicon では plamo3 ネイティブ対応かつアイドルCPU 0% の MLX-4bit (Metal直結) を第一優先にする
    if [[ "$(uname -m)" == "arm64" ]]; then
        if [[ ! -x "${VENV_DIR}/bin/python" ]]; then
            write_status "starting" "${REQ_SIZE}" "mlx-lm" "PLaMo 3 (${SIZE_LABEL}) Apple MLX 環境を初期化中…"
            if command -v uv >/dev/null 2>&1; then
                uv venv "${VENV_DIR}" >>"${LOG_FILE}" 2>&1 || python3 -m venv "${VENV_DIR}" >>"${LOG_FILE}" 2>&1
                uv pip install --python "${VENV_DIR}/bin/python" -U "huggingface_hub[hf_xet]" mlx-lm >>"${LOG_FILE}" 2>&1
            else
                python3 -m venv "${VENV_DIR}" >>"${LOG_FILE}" 2>&1
                "${VENV_DIR}/bin/pip" install -q -U pip "huggingface_hub[hf_xet]" mlx-lm >>"${LOG_FILE}" 2>&1
            fi
        fi

        if [[ ! -f "${MODEL_DIR}/config.json" ]]; then
            write_status "downloading" "${REQ_SIZE}" "mlx-lm" "PLaMo 3 (${SIZE_LABEL} MLX-4bit) 重みをダウンロード中…"
            "${VENV_DIR}/bin/python" - "${MLX_REPO}" "${MODEL_DIR}" >>"${LOG_FILE}" 2>&1 <<'PY'
import sys
from huggingface_hub import snapshot_download
snapshot_download(repo_id=sys.argv[1], local_dir=sys.argv[2])
PY
        fi

        if [[ -f "${MODEL_DIR}/requirements-mlx.txt" ]] && [[ ! -f "${MODEL_DIR}/.deps_installed" ]]; then
            write_status "starting" "${REQ_SIZE}" "mlx-lm" "PLaMo 3 (${SIZE_LABEL}) 依存ライブラリを確認中…"
            if command -v uv >/dev/null 2>&1; then
                uv pip install --python "${VENV_DIR}/bin/python" -r "${MODEL_DIR}/requirements-mlx.txt" >>"${LOG_FILE}" 2>&1
            else
                "${VENV_DIR}/bin/pip" install -q -r "${MODEL_DIR}/requirements-mlx.txt" >>"${LOG_FILE}" 2>&1
            fi
            touch "${MODEL_DIR}/.deps_installed"
        fi

        write_status "starting" "${REQ_SIZE}" "mlx-lm" "PLaMo 3 (${SIZE_LABEL} MLX-4bit) を Metal へロード中…"
        : > "${LOG_FILE}"
        cd "${MODEL_DIR}"
        if [[ -f "serve.py" ]]; then
            nohup "${VENV_DIR}/bin/python" serve.py \
                --model . \
                --trust-remote-code \
                --decode-concurrency 1 \
                --prompt-concurrency 1 \
                --prompt-cache-bytes 256MB \
                --host 127.0.0.1 \
                --port "${PORT}" \
                >>"${LOG_FILE}" 2>&1 &
        else
            nohup "${VENV_DIR}/bin/python" -m mlx_lm.server \
                --model . \
                --trust-remote-code \
                --host 127.0.0.1 \
                --port "${PORT}" \
                >>"${LOG_FILE}" 2>&1 &
        fi
        SRV_PID=$!
        echo "${SRV_PID}" > "${PID_FILE}"
        echo "${REQ_SIZE}" > "${SIZE_FILE}"

        for _ in {1..120}; do
            if is_port_ready; then
                write_status "ready" "${REQ_SIZE}" "mlx-lm" "PLaMo 3 (${SIZE_LABEL}) 稼働中"
                exit 0
            fi
            if ! kill -0 "${SRV_PID}" 2>/dev/null; then
                rm -f "${PID_FILE}" "${SIZE_FILE}"
                break
            fi
            sleep 0.5
        done
    fi

    # フォールバック: llama-server（CPUスレッド暴走防止のため -t 2 -tb 2 -ngl 99 を強制）
    if command -v llama-server >/dev/null 2>&1; then
        write_status "starting" "${REQ_SIZE}" "llama-server" "PLaMo 3 (${SIZE_LABEL} GGUF) を起動中…"
        nohup llama-server \
            -hf "${GGUF_REPO}" \
            --host 127.0.0.1 \
            --port "${PORT}" \
            -c 4096 \
            -np 1 \
            -t 2 \
            -tb 2 \
            -ngl 99 \
            --alias plamo-3-translate \
            --jinja \
            --no-prefill-assistant \
            >>"${LOG_FILE}" 2>&1 &
        SRV_PID=$!
        echo "${SRV_PID}" > "${PID_FILE}"
        echo "${REQ_SIZE}" > "${SIZE_FILE}"

        for _ in {1..180}; do
            if is_port_ready; then
                write_status "ready" "${REQ_SIZE}" "llama-server" "PLaMo 3 (${SIZE_LABEL}) 稼働中"
                exit 0
            fi
            if ! kill -0 "${SRV_PID}" 2>/dev/null; then
                rm -f "${PID_FILE}" "${SIZE_FILE}"
                break
            fi
            sleep 1.0
        done
    fi

    write_status "error" "${REQ_SIZE}" "none" "PLaMo 3 (${SIZE_LABEL}) の起動に失敗しました（.runtime/plamo_server.log を確認）"
) >/dev/null 2>&1 &
