#!/bin/sh

set -eu
umask 077
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage:
  inspect-pkg.sh [--require-signed | --require-unsigned] PACKAGE

Lists package signature and payload, expands it, and verifies that a native,
Explorer-enabled executable is installed at /usr/local/bin/cosmos-emulator.
EOF
}

die() {
    printf 'inspect-pkg.sh: %s\n' "$*" >&2
    exit 1
}

signature_requirement=any
package=

while [ "$#" -gt 0 ]; do
    case "$1" in
        --require-signed)
            [ "$signature_requirement" = any ] ||
                die "signature requirement specified more than once"
            signature_requirement=signed
            shift
            ;;
        --require-unsigned)
            [ "$signature_requirement" = any ] ||
                die "signature requirement specified more than once"
            signature_requirement=unsigned
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        -*)
            die "unknown argument: $1"
            ;;
        *)
            [ -z "$package" ] || die "only one package may be inspected"
            package=$1
            shift
            ;;
    esac
done

[ -n "$package" ] || die "PACKAGE is required"
command -v pkgutil >/dev/null 2>&1 || die "pkgutil is required (run this on macOS)"
command -v lipo >/dev/null 2>&1 || die "lipo is required (run this on macOS)"
command -v strings >/dev/null 2>&1 || die "strings is required (run this on macOS)"
[ -f "$package" ] || die "package is not a regular file: $package"

package_dir=$(dirname "$package")
package_name=$(basename "$package")
package_dir=$(CDPATH= cd -P -- "$package_dir" && pwd) ||
    die "could not resolve package directory: $package_dir"
package="$package_dir/$package_name"

work_base="$package_dir/.${package_name}.inspect.$$"
counter=0
while :; do
    work_dir="${work_base}.${counter}"
    if mkdir -m 700 "$work_dir" 2>/dev/null; then
        break
    fi
    counter=$((counter + 1))
    [ "$counter" -lt 100 ] || die "could not create a private inspection directory"
done

cleanup() {
    if [ -n "${work_dir:-}" ] && [ -d "$work_dir" ]; then
        rm -rf -- "$work_dir"
    fi
}
trap cleanup EXIT
trap 'exit 128' HUP INT TERM

printf '%s\n' 'Package signature:'
signature_output="$work_dir/signature.txt"
if pkgutil --check-signature "$package" >"$signature_output" 2>&1; then
    signature_command_ok=yes
else
    signature_command_ok=no
fi
if grep -Eq '^[[:space:]]*Status: no signature[[:space:]]*$' "$signature_output"; then
    signature_state=unsigned
elif [ "$signature_command_ok" = yes ] &&
    grep -Eq '^[[:space:]]*Status: signed([[:space:]]|$)' "$signature_output"; then
    signature_state=signed
else
    cat "$signature_output"
    die "package signature is invalid or could not be inspected"
fi
cat "$signature_output"

case "$signature_requirement:$signature_state" in
    signed:unsigned) die "a valid package signature is required" ;;
    unsigned:signed) die "package is signed but an unsigned package is required" ;;
esac

printf '\n%s\n' 'Payload:'
payload_listing=$(pkgutil --payload-files "$package") ||
    die "pkgutil could not inspect the package payload"
printf '%s\n' "$payload_listing"
printf '%s\n' "$payload_listing" |
    grep -Eq '^(\./)?usr/local/bin/cosmos-emulator$' ||
    die "package does not install usr/local/bin/cosmos-emulator"

expanded="$work_dir/expanded"
pkgutil --expand-full "$package" "$expanded" ||
    die "pkgutil could not expand the package"
binary="$expanded/Payload/usr/local/bin/cosmos-emulator"
[ -f "$binary" ] || die "expanded package is missing the expected binary"
[ -x "$binary" ] || die "packaged binary is not executable"

case "$(uname -m)" in
    arm64|x86_64) native_arch=$(uname -m) ;;
    *) die "unsupported macOS runner architecture: $(uname -m)" ;;
esac
binary_archs=$(lipo -archs "$binary") ||
    die "could not inspect packaged Mach-O architectures"
case " $binary_archs " in
    *" $native_arch "*) ;;
    *) die "packaged binary is not a native $native_arch Mach-O executable" ;;
esac
strings "$binary" | grep -Fq '/explorer/favicon.svg' ||
    die "packaged binary does not contain the embedded Explorer assets"

printf '\nPackage validation passed (%s).\n' "$(
    printf '%s' "$signature_state"
)"
