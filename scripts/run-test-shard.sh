#!/usr/bin/env bash
set -euo pipefail

# Keep the long Integration suite in four deterministic, independently runnable
# pieces.  Slow classes are placed from the 2026-10-12 CI timings; the remaining
# classes use a small stable name hash so a newly added class is assigned automatically.

readonly integration_project='tests/BeeMemoryBank.Integration.Tests/BeeMemoryBank.Integration.Tests.csproj'

usage() {
    echo "usage: $0 <projects|run|coverage|self-test|integration-batch> <0|1|2|3>" >&2
    exit 64
}

[[ $# -eq 2 ]] || usage
command=$1
shard=$2
[[ $shard =~ ^[0-3]$ ]] || usage

projects_for_shard() {
    case "$1" in
        0) cat <<'PROJECTS'
tests/BeeMemoryBank.AppPaths.Tests/BeeMemoryBank.AppPaths.Tests.csproj
tests/BeeMemoryBank.Core.Tests/BeeMemoryBank.Core.Tests.csproj
tests/BeeMemoryBank.Hosting.AspNetCore.Tests/BeeMemoryBank.Hosting.AspNetCore.Tests.csproj
tests/BeeMemoryBank.Hosting.Tests/BeeMemoryBank.Hosting.Tests.csproj
tests/BeeMemoryBank.Migrator.Tests/BeeMemoryBank.Migrator.Tests.csproj
tests/BeeMemoryBank.Node.Tests/BeeMemoryBank.Node.Tests.csproj
PROJECTS
            ;;
        1) cat <<'PROJECTS'
tests/BeeMemoryBank.Blind.AppCore.Tests/BeeMemoryBank.Blind.AppCore.Tests.csproj
tests/BeeMemoryBank.BlindDesktop.MacOS.Tests/BeeMemoryBank.BlindDesktop.MacOS.Tests.csproj
tests/BeeMemoryBank.BlindDesktop.Tests/BeeMemoryBank.BlindDesktop.Tests.csproj
tests/BeeMemoryBank.BlindIos.Tests/BeeMemoryBank.BlindIos.Tests.csproj
tests/BeeMemoryBank.FullIos.Tests/BeeMemoryBank.FullIos.Tests.csproj
tests/BeeMemoryBank.MacFullPackage.Tests/BeeMemoryBank.MacFullPackage.Tests.csproj
tests/BeeMemoryBank.Sync.Tests/BeeMemoryBank.Sync.Tests.csproj
PROJECTS
            ;;
        2) cat <<'PROJECTS'
tests/BeeMemoryBank.BlindConsole.Tests/BeeMemoryBank.BlindConsole.Tests.csproj
tests/BeeMemoryBank.BlindNode.Tests/BeeMemoryBank.BlindNode.Tests.csproj
tests/BeeMemoryBank.Cli.Tests/BeeMemoryBank.Cli.Tests.csproj
tests/BeeMemoryBank.Crypto.Tests/BeeMemoryBank.Crypto.Tests.csproj
tests/BeeMemoryBank.Desktop.Tests/BeeMemoryBank.Desktop.Tests.csproj
tests/BeeMemoryBank.Storage.Tests/BeeMemoryBank.Storage.Tests.csproj
PROJECTS
            ;;
        3) cat <<'PROJECTS'
tests/BeeMemoryBank.Platforms.Apple.Tests/BeeMemoryBank.Platforms.Apple.Tests.csproj
tests/BeeMemoryBank.Profiles.Tests/BeeMemoryBank.Profiles.Tests.csproj
tests/BeeMemoryBank.Rekey.Tests/BeeMemoryBank.Rekey.Tests.csproj
tests/BeeMemoryBank.ReleaseTool.Tests/BeeMemoryBank.ReleaseTool.Tests.csproj
tests/BeeMemoryBank.Search.Tests/BeeMemoryBank.Search.Tests.csproj
tests/BeeMemoryBank.SeedGen.Tests/BeeMemoryBank.SeedGen.Tests.csproj
tests/BeeMemoryBank.Updater.Tests/BeeMemoryBank.Updater.Tests.csproj
PROJECTS
            ;;
    esac
}

# The largest classes in the last full CI run, balanced before the deterministic
# fallback is applied.  Names not here use their stable POSIX cksum bucket.
fixed_class_shard() {
    case "$1" in
        RestoreScenarioTests|RestoreWizardPageTests|SealMigrationApiTests|EndpointAuthGuardrailTests|BlindResticDefaultPasswordTests|BlindPhoneReplicaClientTests|MetadataKeyRotationTests|SnapshotSealedCreateTests) echo 0 ;;
        AndroidBackupRestoreScenarioTests|RekeyKeyMaterialTests|RecoveryBoxLifecycleTests|ApiIntegrationTests|BaselineRestoreTests|BlindPhoneFormat2Tests|SealedProcessTests|JoinPeerTlsEndpointTests|McpSessionGuardMiddlewareTests) echo 1 ;;
        RecoverAccessEndpointTests|SealedApiGateTests|SealRollbackTests|SessionCookieSecureFlagTests|BlindPairingSeedTests|BlindBackupEndpointsTests|BlindPhonePullClientTests|BlindConsolePasswordTests|SealedRelayTests) echo 2 ;;
        BlindStartupTests|RecoveryWarningsPageTests|RecoverAccessPageTests|SealPreflightApiTests|RekeyReportEndpointTests|JoinByCodeThroughTheDoorTests|BlindPhoneReplicaVerificationTests|BlindConsoleAuthTests|BaselineRestoreSourcesTests|ServerAutoUnlockEndpointsTests) echo 3 ;;
        *) return 1 ;;
    esac
}

class_shard() (
    local fixed hash=0 character i
    if fixed=$(fixed_class_shard "$1"); then
        printf '%s\n' "$fixed"
    else
        # Hash bytes, not locale-dependent characters, so the result is stable
        # on every GitHub runner architecture and locale.
        LC_ALL=C
        export LC_ALL
        for ((i = 0; i < ${#1}; i++)); do
            printf -v character '%d' "'${1:i:1}"
            hash=$(((hash * 31 + character) % 2147483647))
        done
        printf '%s\n' "$((hash % 4))"
    fi
)

# These arrays are populated only by parse_integration_discovery.  Do not use a
# permissive namespace-only filter here: every line after the test-list header
# must be a supported Integration test and must receive precisely one owner.
declare -a discovered_tests=()
declare -a discovered_classes=()

class_from_integration_test() {
    local test_name=$1
    local integration_test_pattern='^BeeMemoryBank\.Integration\.Tests\.([^.]*Tests[^.]*)\..+$'
    if [[ $test_name =~ $integration_test_pattern ]]; then
        printf '%s\n' "${BASH_REMATCH[1]}"
    else
        return 1
    fi
}

parse_integration_discovery() {
    local output=$1 raw line class_name owner header_seen=0
    local -a invalid_lines=()
    discovered_tests=()
    discovered_classes=()

    while IFS= read -r raw || [[ -n $raw ]]; do
        line=${raw#"${raw%%[![:space:]]*}"}
        line=${line%"${line##*[![:space:]]}"}
        if (( ! header_seen )); then
            [[ $line == 'The following Tests are available:' ]] && header_seen=1
            continue
        fi
        [[ -z $line ]] && continue
        if ! class_name=$(class_from_integration_test "$line"); then
            invalid_lines+=("$line")
            continue
        fi
        if ! owner=$(class_shard "$class_name") || [[ ! $owner =~ ^[0-3]$ ]]; then
            invalid_lines+=("$line (no unique shard owner)")
            continue
        fi
        discovered_tests+=("$line")
        discovered_classes+=("$class_name")
    done <<< "$output"

    if (( ! header_seen )); then
        echo 'Integration test discovery did not print its test-list header' >&2
        return 1
    fi
    if ((${#invalid_lines[@]})); then
        echo 'Integration discovery has unowned or malformed test names:' >&2
        printf '  %s\n' "${invalid_lines[@]}" >&2
        return 1
    fi
    if ((${#discovered_tests[@]} == 0)); then
        echo 'Integration test discovery returned no accepted tests' >&2
        return 1
    fi
    mapfile -t discovered_classes < <(printf '%s\n' "${discovered_classes[@]}" | LC_ALL=C sort -u)
}

discover_integration_tests() {
    local filter=$1 output
    if ! output=$(dotnet test "$integration_project" --no-build -c Release --list-tests --filter "$filter"); then
        echo 'Integration test discovery command failed' >&2
        return 1
    fi
    parse_integration_discovery "$output"
}

filter_for_classes_in_shard() {
    local wanted_shard=$1
    shift
    local class_name class_filter='' owner
    for class_name in "$@"; do
        owner=$(class_shard "$class_name")
        if [[ $owner == "$wanted_shard" ]]; then
            local term="FullyQualifiedName~BeeMemoryBank.Integration.Tests.${class_name}."
            class_filter=${class_filter:+"$class_filter|"}$term
        fi
    done
    [[ -n $class_filter ]] || {
        echo "no Integration classes assigned to shard $wanted_shard" >&2
        return 1
    }
    printf '%s\n' "$class_filter"
}

non_timing_filter() {
    printf 'Category!=Timing&(%s)\n' "$1"
}

integration_filter() {
    local wanted_shard=$1
    discover_integration_tests 'Category!=Timing'
    filter_for_classes_in_shard "$wanted_shard" "${discovered_classes[@]}"
}

# Some environments cap a single command at 20 minutes.  This opt-in mode lets a slow Integration
# shard be resumed as deterministic class batches after its workflow-equivalent
# build and coverage check have completed.  CI does not set this variable.
integration_batch_filter() {
    local requested_shard=$1 batch_spec=${BMB_INTEGRATION_BATCH:-}
    local batch_index batch_count owner_index=0 class_name class_filter='' owner
    [[ $batch_spec =~ ^[0-9]+/[1-9][0-9]*$ ]] || {
        echo 'BMB_INTEGRATION_BATCH must be INDEX/COUNT' >&2
        return 1
    }
    batch_index=${batch_spec%/*}
    batch_count=${batch_spec#*/}
    ((batch_index < batch_count)) || {
        echo 'BMB_INTEGRATION_BATCH index must be smaller than count' >&2
        return 1
    }

    discover_integration_tests 'Category!=Timing'
    for class_name in "${discovered_classes[@]}"; do
        owner=$(class_shard "$class_name")
        [[ $owner == "$requested_shard" ]] || continue
        if ((owner_index % batch_count == batch_index)); then
            local term="FullyQualifiedName~BeeMemoryBank.Integration.Tests.${class_name}."
            class_filter=${class_filter:+"$class_filter|"}$term
        fi
        ((owner_index += 1))
    done
    [[ -n $class_filter ]] || {
        echo "no Integration classes assigned to shard $requested_shard batch $batch_spec" >&2
        return 1
    }
    printf '%s\n' "$class_filter"
}

test_project() {
    local project=$1
    local filter=$2
    if ! dotnet test "$project" --no-build -c Release --verbosity normal --filter "$filter" \
        --blame-hang-timeout 15m --blame-hang-dump-type none; then
        failed_projects+=("$project")
    fi
}

verify_project_ownership() {
    local expected actual
    expected=$(find tests -mindepth 2 -maxdepth 2 -name '*.csproj' \
        -not -path '*/BeeMemoryBank.BlindMobile.Tests/*' \
        -not -path '*/BeeMemoryBank.Integration.Tests/*' \
        -not -path '*/BeeMemoryBank.Node.Tests.StubProcess/*' | LC_ALL=C sort)
    actual=$(for n in 0 1 2 3; do projects_for_shard "$n"; done | LC_ALL=C sort)
    if [[ $expected != "$actual" ]]; then
        echo 'test-project shard map is incomplete or duplicated:' >&2
        diff -u <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") >&2 || true
        exit 1
    fi
}

coverage() {
    verify_project_ownership

    local filter class_name shard_tests
    discover_integration_tests 'Category!=Timing'
    local -a all_tests=("${discovered_tests[@]}")
    local -a coverage_classes=("${discovered_classes[@]}")
    local -a combined_tests=()
    local -a sorted_all_tests=()
    mapfile -t sorted_all_tests < <(printf '%s\n' "${all_tests[@]}" | LC_ALL=C sort)
    if ((${#all_tests[@]} != ${#sorted_all_tests[@]})) || [[ $(printf '%s\n' "${sorted_all_tests[@]}" | LC_ALL=C uniq -d | wc -l | tr -d ' ') != 0 ]]; then
        echo 'Integration discovery listed a test more than once' >&2
        return 1
    fi

    for n in 0 1 2 3; do
        filter=$(filter_for_classes_in_shard "$n" "${coverage_classes[@]}")
        discover_integration_tests "$(non_timing_filter "$filter")"
        shard_tests=${#discovered_tests[@]}
        ((shard_tests > 0)) || { echo "shard $n selects no Integration tests" >&2; return 1; }
        printf 'Integration shard %s: %s tests\n' "$n" "$shard_tests"
        combined_tests+=("${discovered_tests[@]}")
    done

    if [[ $(printf '%s\n' "${combined_tests[@]}" | LC_ALL=C sort | LC_ALL=C uniq -d | wc -l | tr -d ' ') != 0 ]]; then
        echo 'an Integration test is selected by more than one shard' >&2
        return 1
    fi
    if ! diff -u <(printf '%s\n' "${sorted_all_tests[@]}") <(printf '%s\n' "${combined_tests[@]}" | LC_ALL=C sort); then
        echo 'Integration shard filters do not cover the current non-Timing test set exactly once' >&2
        return 1
    fi
    echo "Integration coverage: ${#all_tests[@]} non-Timing tests exactly once."
}

self_test() {
    local valid_discovery=$'The following Tests are available:\n    BeeMemoryBank.Integration.Tests.FooTests.Case\n    BeeMemoryBank.Integration.Tests.FooTests2.Case\n    BeeMemoryBank.Integration.Tests.Outer+InnerTests.Case\n    BeeMemoryBank.Integration.Tests.GenericTests`1.Case'
    local foo_prefix='BeeMemoryBank.Integration.Tests.FooTests.'
    local class_name owner

    parse_integration_discovery "$valid_discovery"
    [[ ${#discovered_tests[@]} == 4 ]] || { echo 'synthetic valid discovery count changed' >&2; return 1; }
    for class_name in "${discovered_classes[@]}"; do
        owner=$(class_shard "$class_name")
        [[ $owner =~ ^[0-3]$ ]] || { echo "synthetic class lacks owner: $class_name" >&2; return 1; }
    done
    [[ BeeMemoryBank.Integration.Tests.FooTests2.Case != "$foo_prefix"* ]] || {
        echo 'prefix filter would overlap FooTests2' >&2
        return 1
    }
    [[ $(non_timing_filter 'a|b') == 'Category!=Timing&(a|b)' ]] || {
        echo 'generated OR filter lacks grouping parentheses' >&2
        return 1
    }
    if parse_integration_discovery "$valid_discovery"$'\n    BeeMemoryBank.Integration.OtherNamespace.HiddenTests.Case'; then
        echo 'synthetic foreign namespace was accepted' >&2
        return 1
    fi
    if parse_integration_discovery 'The following Tests are available:'; then
        echo 'synthetic empty discovery was accepted' >&2
        return 1
    fi
    if parse_integration_discovery 'Test Run Failed'; then
        echo 'synthetic failed discovery was accepted' >&2
        return 1
    fi
    echo 'Synthetic Integration discovery checks passed.'
}

case "$command" in
    projects)
        projects_for_shard "$shard"
        ;;
    run)
        failed_projects=()
        integration_filter_value=$(integration_filter "$shard")
        test_project "$integration_project" "$(non_timing_filter "$integration_filter_value")"
        while IFS= read -r project; do
            [[ -n $project ]] && test_project "$project" 'Category!=Timing'
        done < <(projects_for_shard "$shard")
        if ((${#failed_projects[@]})); then
            printf '::error::test projects with failures: %s\n' "${failed_projects[*]}" >&2
            exit 1
        fi
        ;;
    integration-batch)
        failed_projects=()
        integration_filter_value=$(integration_batch_filter "$shard")
        test_project "$integration_project" "$(non_timing_filter "$integration_filter_value")"
        if ((${#failed_projects[@]})); then
            printf '::error::Integration batch failed: %s\n' "${failed_projects[*]}" >&2
            exit 1
        fi
        ;;
    coverage)
        coverage
        ;;
    self-test)
        self_test
        ;;
    *) usage ;;
esac
