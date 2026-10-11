# Bash startup hooks used by smoke-docker-cleanup.sh to make Docker lifecycle
# operations fail after the real operation has created the test container.

docker() {
    case "${1:-}" in
        run)
            if [ -n "${BMB_DOCKER_AGENT_LABEL:-}" ]; then
                set -- run --label "bmb.agent=$BMB_DOCKER_AGENT_LABEL" "${@:2}"
            fi
            if [ "${BMB_SMOKE_TEST_FAIL_RUN:-0}" = 1 ]; then
                command docker "$@"
                rc=$?
                [ "$rc" -eq 0 ] && return 42
                return "$rc"
            fi
            ;;
        stop)
            if [ "${BMB_SMOKE_TEST_FAIL_STOP:-0}" = 1 ]; then
                return 42
            fi
            ;;
        rm)
            if [ "${BMB_SMOKE_TEST_FAIL_REMOVE:-0}" = 1 ] && [ "${2:-}" != -f ]; then
                return 42
            fi
            ;;
    esac
    command docker "$@"
}

# The real Ctrl-C path is signal-driven. This deterministic hook models the
# observed Bash case where the EXIT trap receives status 0 after SIGINT; the
# production script must install its own non-zero INT trap before this sleep.
if [ "${BMB_SMOKE_TEST_INTERRUPT:-0}" = 1 ]; then
    trap 'exit 0' INT
    sleep() {
        kill -INT "$$"
    }
fi
