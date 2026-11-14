#!/usr/bin/env bash
# Linux probe: run INSIDE the sandbox. It tries what an info-stealer would and prints whether
# each attempt was allowed. Nothing is read out, stored or sent.
#   jvm-sandbox --rw <dir> -- bash test/probe.sh <dir> <a file outside the sandbox> [nonet]
allowed=$1 secret=$2 net=${3:-net}
fail=0

check() { # check <description> <allowed|denied> <command...>
    local what=$1 expect=$2 got
    shift 2
    if "$@" >/dev/null 2>&1; then got=allowed; else got=denied; fi
    if [ "$got" = "$expect" ]; then echo "PASS $what: $got"; else echo "FAIL $what: $got (expected $expect)"; fail=1; fi
}
readable() { [ -r "$1" ] && head -c 1 "$1"; }
# in its own process namespace the sandbox helper is pid 1; outside it would be init/systemd
other_processes() { ! grep -qa bwrap /proc/1/cmdline; }
connect() { timeout 5 bash -c "exec 3<>/dev/tcp/$1/$2"; }

check "write game dir" allowed bash -c "echo hi > '$allowed/probe.txt'"
check "read game dir" allowed readable "$allowed/probe.txt"
check "temp file" allowed bash -c 'f=$(mktemp) && rm "$f"'
check "read secret outside sandbox" denied readable "$secret"
for t in .ssh .gnupg .config/discord .config/google-chrome .mozilla .local/share/keyrings .config/Exodus .electrum .steam .bash_history .gitconfig; do
    check "read ~/$t" denied test -e "$HOME/$t"
done
check "see other processes" denied other_processes
check "session D-Bus (keyring, secret service)" denied test -e "${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/bus"
check "ssh-agent" denied test -n "${SSH_AUTH_SOCK:-}"
check "write to /usr" denied bash -c 'echo x > /usr/sbx_probe'
# Writing into the (empty, throwaway) home works but must not reach the real one: after the
# run, check outside the sandbox that ~/.config/autostart/sbx_probe.desktop does not exist.
mkdir -p "$HOME/.config/autostart" 2>/dev/null && echo x > "$HOME/.config/autostart/sbx_probe.desktop" 2>/dev/null
if [ "$net" = nonet ]; then check "internet" denied connect 1.1.1.1 80; else check "internet" allowed connect 1.1.1.1 80; fi
exit $fail
