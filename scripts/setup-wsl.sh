#!/usr/bin/env bash
#
# One-step setup for the notes service inside a WSL 2 distribution.
#
# Installs Docker Engine and Docker Compose when they are missing (Ubuntu),
# creates a private .env with random credentials, builds and starts the API and
# PostgreSQL with Docker Compose, then waits until the service is healthy.
# Safe to rerun: an existing .env and database volume are always kept.

if [ -z "${BASH_VERSION:-}" ]; then
    printf 'Run this script with bash: bash scripts/setup-wsl.sh\n' >&2
    exit 1
fi

set -Eeuo pipefail

readonly COMPOSE_PROJECT=notes-service
readonly DATA_VOLUME="${COMPOSE_PROJECT}_notes-data"
readonly HEALTH_TIMEOUT_SECONDS=120
readonly DOCKER_START_TIMEOUT_SECONDS=60

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
run_tests=false
is_wsl=false
apt_updated=false
uses_docker_desktop=false
docker_via_sudo=false
service_address=''
api_container=''
DOCKER=(docker)

if [[ -t 1 && -z ${NO_COLOR:-} ]]; then
    bold=$'\e[1m' red=$'\e[31m' yellow=$'\e[33m' reset=$'\e[0m'
else
    bold='' red='' yellow='' reset=''
fi

step() { printf '\n%s==> %s%s\n' "$bold" "$1" "$reset"; }
info() { printf '    %s\n' "$@"; }
warn() { printf '%swarning:%s %s\n' "$yellow" "$reset" "$1" >&2; }

die() {
    printf '%serror:%s %s\n' "$red" "$reset" "$1" >&2
    shift
    if (($#)); then
        printf '       %s\n' "$@" >&2
    fi
    exit 1
}

usage() {
    cat <<'EOF'
Usage: bash scripts/setup-wsl.sh [--run-tests]

Sets up and starts the notes service in this WSL 2 distribution:
  1. Checks for WSL 2 and a checkout on the Linux filesystem.
  2. Installs Docker Engine, Docker Compose V2, Buildx, curl, and OpenSSL from
     the Ubuntu archive when they are missing (using sudo), then starts Docker.
  3. Creates a private .env with random credentials unless one already exists.
  4. Builds and starts the API and PostgreSQL, then waits for /health.

Rerun it at any time, for example after `git pull` or a WSL restart. It keeps
an existing .env and database volume, and it never prints credentials.

Options:
  --run-tests  Also run the automated test suite in containers after startup.
  -h, --help   Show this help.
EOF
}

parse_args() {
    while (($#)); do
        case $1 in
            --run-tests) run_tests=true ;;
            -h | --help)
                usage
                exit 0
                ;;
            *) die "Unknown option: $1" "Run 'bash scripts/setup-wsl.sh --help' for usage." ;;
        esac
        shift
    done
}

as_root() {
    if ((EUID == 0)); then
        "$@"
    elif command -v sudo >/dev/null 2>&1; then
        sudo "$@"
    else
        die "Root privileges are required to run: $*" "Install sudo, or rerun this script as root."
    fi
}

check_platform() {
    [[ $(uname -s) == Linux ]] ||
        die "Run this script from a shell inside your WSL 2 distribution, not from Windows."

    if ((EUID == 0)) && [[ -n ${SUDO_USER:-} ]]; then
        die "Run this script as your regular user, without sudo." \
            "It asks for sudo only when needed, and keeps the generated .env readable by you."
    fi

    local kernel
    kernel=$(uname -r)
    # WSL 1 kernels report versions such as 4.4.0-19041-Microsoft and cannot run Docker.
    if [[ $kernel == *-Microsoft ]]; then
        die "This distribution runs under WSL 1, which cannot run Docker Engine." \
            "Convert it from Windows PowerShell with: wsl --set-version ${WSL_DISTRO_NAME:-<distribution>} 2"
    fi

    if [[ -n ${WSL_DISTRO_NAME:-} || ${kernel,,} == *microsoft* ]]; then
        is_wsl=true
        info "WSL 2 distribution: ${WSL_DISTRO_NAME:-unknown} (kernel $kernel)"
    else
        warn "This does not look like WSL 2; continuing as a regular Linux host."
    fi
}

check_checkout_location() {
    info "Checkout: $repo_root"
    [[ $is_wsl == true ]] || return 0

    local fstype origin
    fstype=$(findmnt --noheadings --output FSTYPE --target "$repo_root" 2>/dev/null || true)
    if [[ $repo_root == /mnt/[a-zA-Z]/* || $fstype == 9p || $fstype == drvfs || $fstype == virtiofs ]]; then
        origin=$(git -C "$repo_root" remote get-url origin 2>/dev/null | sed -E 's#://[^/@]*@#://#' || true)
        die "This checkout is on a Windows drive, but the service must run from the Linux filesystem." \
            "Clone it into your WSL home directory and run the script from there:" \
            "  git clone ${origin:-<repository-url>} ~/notes-service" \
            "  cd ~/notes-service && bash scripts/setup-wsl.sh"
    fi
}

is_ubuntu_family() {
    local id='' id_like=''
    if [[ -r /etc/os-release ]]; then
        # shellcheck source=/dev/null
        id=$(. /etc/os-release && printf '%s' "${ID:-}")
        # shellcheck source=/dev/null
        id_like=$(. /etc/os-release && printf '%s' "${ID_LIKE:-}")
    fi
    [[ $id == ubuntu || " $id_like " == *" ubuntu "* ]]
}

package_installed() {
    # shellcheck disable=SC2016 # ${Status} is a dpkg-query field, not a shell expansion.
    [[ $(dpkg-query --show --showformat='${Status}' "$1" 2>/dev/null || true) == 'install ok installed' ]]
}

apt_install() {
    command -v apt-get >/dev/null 2>&1 ||
        die "Missing required packages: $*" "Install them with your distribution's package manager, then rerun."

    if [[ $apt_updated == false ]]; then
        info "Updating package lists (sudo may ask for your password)"
        as_root apt-get -q update
        apt_updated=true
    fi

    local package unavailable=()
    for package in "$@"; do
        apt-cache show "$package" >/dev/null 2>&1 || unavailable+=("$package")
    done
    if ((${#unavailable[@]})); then
        die "These packages are not available from your apt sources: ${unavailable[*]}" \
            "On Ubuntu, enable the universe component and rerun: sudo add-apt-repository universe"
    fi

    info "Installing: $*"
    as_root env DEBIAN_FRONTEND=noninteractive apt-get -q -o DPkg::Lock::Timeout=300 install -y "$@"
}

ensure_base_tools() {
    local tool missing=()
    for tool in curl openssl; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done
    if ((${#missing[@]})); then
        step "Installing required tools"
        apt_install ca-certificates "${missing[@]}"
    fi
}

# Docker Desktop's WSL integration links its CLI into the distribution. The link
# dangles while Docker Desktop is stopped, and installing Docker Engine here
# would conflict with it.
docker_desktop_cli() {
    local candidate
    for candidate in /usr/bin/docker /usr/local/bin/docker; do
        if [[ -L $candidate && $(readlink "$candidate") == */docker-desktop/* ]]; then
            return 0
        fi
    done
    return 1
}

select_docker_command() {
    if docker info >/dev/null 2>&1; then
        DOCKER=(docker)
        docker_via_sudo=false
    elif ((EUID != 0)) && command -v sudo >/dev/null 2>&1 && sudo docker info >/dev/null 2>&1; then
        DOCKER=(sudo docker)
        docker_via_sudo=true
    else
        return 1
    fi
}

docker_start_failed() {
    local log_command='sudo tail -n 50 /var/log/docker.log'
    if [[ -d /run/systemd/system ]]; then
        log_command='sudo journalctl -u docker --no-pager -n 50'
    fi
    die "Docker Engine did not start." \
        "Inspect its log with: $log_command" \
        "On WSL, an outdated kernel can prevent Docker from starting; from Windows, run: wsl --update"
}

install_docker() {
    is_ubuntu_family ||
        die "Docker is not installed, and this script installs it automatically only on Ubuntu." \
            "Install Docker Engine with the Compose V2 plugin (https://docs.docker.com/engine/install/)," \
            "or enable Docker Desktop's WSL integration, then rerun this script."
    step "Installing Docker Engine, Docker Compose V2, and Buildx from the Ubuntu archive"
    apt_install docker.io docker-compose-v2 docker-buildx
}

start_docker_engine() {
    if select_docker_command; then
        return
    fi

    if [[ -d /run/systemd/system ]]; then
        info "Starting Docker with systemd"
        as_root systemctl enable --now docker.service || docker_start_failed
    elif [[ -x /etc/init.d/docker ]]; then
        info "Starting Docker with its init script (systemd is not enabled in this distribution)"
        as_root /etc/init.d/docker start || docker_start_failed
    else
        # Ubuntu's docker.io package ships only systemd units, so start the daemon in its own session.
        info "Starting dockerd in the background (systemd is not enabled in this distribution)"
        as_root sh -c 'setsid dockerd >>/var/log/docker.log 2>&1 </dev/null &' || docker_start_failed
    fi

    local waited=0
    until select_docker_command; do
        ((waited < DOCKER_START_TIMEOUT_SECONDS)) || docker_start_failed
        sleep 1
        waited=$((waited + 1))
    done
}

ensure_compose() {
    if "${DOCKER[@]}" compose version >/dev/null 2>&1; then
        return
    fi

    if [[ $uses_docker_desktop == false ]] && is_ubuntu_family && package_installed docker.io; then
        step "Installing Docker Compose V2 and Buildx from the Ubuntu archive"
        apt_install docker-compose-v2 docker-buildx
        if "${DOCKER[@]}" compose version >/dev/null 2>&1; then
            return
        fi
    fi

    die "Docker Compose V2 ('docker compose') is not available." \
        "Install the Compose plugin that matches your Docker installation, then rerun:" \
        "  Ubuntu docker.io package: sudo apt-get install docker-compose-v2" \
        "  Docker CE packages:       sudo apt-get install docker-compose-plugin"
}

ensure_docker() {
    step "Preparing Docker"
    if docker_desktop_cli; then
        uses_docker_desktop=true
        select_docker_command ||
            die "This distribution uses Docker Desktop's WSL integration, but Docker Desktop is not responding." \
                "Start Docker Desktop on Windows and rerun. To use a Docker Engine inside this distribution" \
                "instead, turn off Docker Desktop's WSL integration for it first."
        info "Using Docker Desktop's WSL integration"
    else
        if ! command -v docker >/dev/null 2>&1; then
            install_docker
        fi
        start_docker_engine
    fi
    ensure_compose

    local engine_version compose_version
    engine_version=$("${DOCKER[@]}" version --format '{{.Server.Version}}' 2>/dev/null || true)
    compose_version=$("${DOCKER[@]}" compose version --short 2>/dev/null || true)
    compose_version=${compose_version%%+*}
    info "Docker Engine ${engine_version:-unknown}, Docker Compose ${compose_version:-unknown}"
    if [[ $docker_via_sudo == true ]]; then
        info "Your account cannot use the Docker socket directly, so Docker commands run with sudo."
    fi
}

ensure_env() {
    step "Configuring credentials"
    if [[ -f .env ]]; then
        info "Keeping the existing private .env"
        return
    fi

    if "${DOCKER[@]}" volume inspect "$DATA_VOLUME" >/dev/null 2>&1; then
        local docker_command="${DOCKER[*]}"
        die "Found the existing database volume '$DATA_VOLUME', but no .env file." \
            "Its database password exists only in the .env that created the volume." \
            "To keep the existing notes, restore that .env to $repo_root and rerun." \
            "To permanently delete all existing notes and start fresh instead, run:" \
            "  $docker_command ps -aq --filter label=com.docker.compose.project=$COMPOSE_PROJECT | xargs -r $docker_command rm -f" \
            "  $docker_command volume rm $DATA_VOLUME"
    fi

    bash scripts/configure-demo.sh
}

start_stack() {
    step "Building and starting the API and PostgreSQL (the first build can take several minutes)"
    if ! "${DOCKER[@]}" compose up --build --detach; then
        die "Docker Compose could not build or start the service." \
            "Check that .env defines every value listed in .env.example." \
            "If NuGet is unreachable, set NUGET_SOURCE in .env (see .env.example) and rerun." \
            "If the HTTP port is already in use, set NOTES_PORT in .env to a free port and rerun."
    fi
}

startup_failed() {
    local logs
    logs=$("${DOCKER[@]}" compose logs --no-color --tail 200 api 2>&1 || true)
    printf '%s\n' "$logs" | tail -n 40 >&2
    if grep -q 'password authentication failed' <<<"$logs"; then
        die "$1" \
            "PostgreSQL rejected the password in .env. It must match the password used when the" \
            "database volume was first created. Restore the original .env and rerun, or erase all" \
            "existing notes and start fresh with:" \
            "  ${DOCKER[*]} compose down --volumes && bash scripts/setup-wsl.sh"
    fi
    die "$1" "See the API log above. After fixing the problem, rerun: bash scripts/setup-wsl.sh"
}

wait_for_health() {
    step "Waiting for the service to report healthy"
    local state restarts baseline='' deadline=$((SECONDS + HEALTH_TIMEOUT_SECONDS))
    api_container=$("${DOCKER[@]}" compose ps --all --quiet api 2>/dev/null || true)
    [[ -n $api_container ]] || startup_failed "The api container was not created."

    while ((SECONDS < deadline)); do
        if [[ -z $service_address ]]; then
            service_address=$("${DOCKER[@]}" compose port api 8080 2>/dev/null | tail -n 1 || true)
        fi
        if [[ -n $service_address ]] &&
            curl --fail --silent --max-time 3 "http://$service_address/health" >/dev/null 2>&1; then
            info "Healthy: http://$service_address/health"
            return
        fi

        state=$("${DOCKER[@]}" inspect --format '{{.State.Status}} {{.RestartCount}}' "$api_container" 2>/dev/null || true)
        read -r state restarts <<<"$state"
        baseline=${baseline:-${restarts:-0}}
        if [[ $state == exited || $state == dead ]] || ((${restarts:-0} > baseline)); then
            startup_failed "The API stopped during startup."
        fi
        sleep 2
    done
    startup_failed "The API did not report healthy within $HEALTH_TIMEOUT_SECONDS seconds."
}

run_test_suite() {
    step "Running the automated tests against an isolated PostgreSQL container"
    mkdir -p TestResults
    local status=0
    "${DOCKER[@]}" compose --profile test run --build --rm tests || status=$?
    "${DOCKER[@]}" compose --profile test rm --stop --force test-db >/dev/null 2>&1 || true
    ((status == 0)) ||
        die "The automated tests failed (exit code $status); the service itself is still running." \
            "Results: $repo_root/TestResults/notes-tests.trx"
    info "All tests passed. Results: $repo_root/TestResults/notes-tests.trx"
}

# Reads only the opt-in flag from the API container, never its credentials.
browser_keys_enabled() {
    local enabled
    [[ -n $api_container ]] || return 1
    enabled=$("${DOCKER[@]}" inspect --format \
        '{{range .Config.Env}}{{if eq (lower .) "demo__enablebrowserkeys=true"}}yes{{end}}{{end}}' \
        "$api_container" 2>/dev/null || true)
    [[ $enabled == yes ]]
}

print_summary() {
    local url="http://localhost:${service_address##*:}" sudo_prefix='' windows_env_path=''
    if [[ $docker_via_sudo == true ]]; then
        sudo_prefix='sudo '
    fi
    if [[ $is_wsl == true ]] && command -v wslpath >/dev/null 2>&1; then
        windows_env_path=$(wslpath -w "$repo_root/.env" 2>/dev/null || true)
    fi

    step "The notes service is running"
    info "Web workbench:  $url/" \
        "API base URL:   $url/api" \
        "Demo API keys:  $repo_root/.env (private; never commit or share it)"
    if [[ -n $windows_env_path ]]; then
        info "                From Windows: $windows_env_path"
    fi
    if [[ $is_wsl == true ]]; then
        info "" "The web workbench also opens in a Windows browser at $url/."
    fi

    if browser_keys_enabled; then
        info "" "One-click demo users are enabled (DEMO_BROWSER_KEYS=true in .env): anyone who can" \
            "open the page can act as every demo user. Set it to false and rerun to turn it off."
    else
        info "" "To act as a demo user, expand \"Manual key and authentication checks\" on the page" \
            "and paste a DEMO_*_KEY value from .env. For one-click demo users on this local page," \
            "set DEMO_BROWSER_KEYS=true in .env and rerun this script; anyone who can open the page" \
            "can then act as every demo user."
    fi

    info "" "Useful commands (run from $repo_root):"
    printf '      %-36s %s\n' \
        "${sudo_prefix}docker compose ps" "Show container status" \
        "${sudo_prefix}docker compose logs -f api" "Follow the API log" \
        "${sudo_prefix}docker compose stop" "Stop the service; notes are kept" \
        "bash scripts/setup-wsl.sh" "Start again, or rebuild after git pull"

    if [[ $docker_via_sudo == true ]]; then
        local relogin='start a new login session'
        if [[ $is_wsl == true ]]; then
            relogin='restart WSL (run wsl --shutdown from Windows)'
        fi
        info "" "To run Docker without sudo, run 'sudo usermod -aG docker \$USER', then $relogin." \
            "Members of the docker group have root-equivalent access to this system."
    fi
    if [[ $is_wsl == true && $uses_docker_desktop == false ]]; then
        info "" "WSL stops this distribution, including the service, shortly after its last terminal" \
            "closes. Keep a WSL terminal open while you use the service."
        if [[ ! -d /run/systemd/system ]]; then
            info "systemd is not enabled, so Docker does not start automatically when WSL restarts;" \
                "rerun this script after a restart."
        fi
    fi
    if [[ -n ${SSH_CONNECTION:-} ]]; then
        info "" "You are connected over SSH. To browse from your own computer, use the SSH tunnel" \
            "described under \"WSL on another machine\" in README.md."
    fi
}

main() {
    parse_args "$@"
    cd "$repo_root"

    step "Checking the environment"
    check_platform
    check_checkout_location
    ensure_base_tools
    ensure_docker
    ensure_env
    start_stack
    wait_for_health
    if [[ $run_tests == true ]]; then
        run_test_suite
    fi
    print_summary
}

trap 'printf "%serror:%s setup failed near line %s.\n" "$red" "$reset" "$LINENO" >&2' ERR

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    main "$@"
fi
