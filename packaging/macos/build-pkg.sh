#!/bin/sh

set -eu
umask 022
export LC_ALL=C

usage() {
    cat <<'EOF'
Usage:
  build-pkg.sh --version VERSION --input BINARY --output PACKAGE
               [--sign IDENTITY]

Builds a macOS installer package containing /usr/local/bin/cosmos-emulator.
The input must be a native Mach-O executable built with the default
Explorer-enabled feature set.

Options:
  --version VERSION  Package version passed to pkgbuild.
  --input BINARY     Explorer-enabled cosmos-emulator binary to package.
  --output PACKAGE   Destination .pkg path (must not already exist).
  --sign IDENTITY    Developer ID Installer identity. Omit for an unsigned pkg.
  -h, --help         Show this help.
EOF
}

die() {
    printf 'build-pkg.sh: %s\n' "$*" >&2
    exit 1
}

version=
input=
output=
identity=

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            [ "$#" -ge 2 ] || die "--version requires a value"
            version=$2
            shift 2
            ;;
        --input)
            [ "$#" -ge 2 ] || die "--input requires a value"
            input=$2
            shift 2
            ;;
        --output)
            [ "$#" -ge 2 ] || die "--output requires a value"
            output=$2
            shift 2
            ;;
        --sign)
            [ "$#" -ge 2 ] || die "--sign requires a value"
            identity=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

[ -n "$version" ] || die "--version is required"
[ -n "$input" ] || die "--input is required"
[ -n "$output" ] || die "--output is required"

case "$version" in
    *[!0-9A-Za-z.+-]*|'') die "version contains unsupported characters: $version" ;;
esac
case "$output" in
    *.pkg) ;;
    *) die "--output must end in .pkg" ;;
esac

command -v pkgbuild >/dev/null 2>&1 || die "pkgbuild is required (run this on macOS)"
command -v pkgutil >/dev/null 2>&1 || die "pkgutil is required (run this on macOS)"
command -v lipo >/dev/null 2>&1 || die "lipo is required (run this on macOS)"
command -v strings >/dev/null 2>&1 || die "strings is required (run this on macOS)"
[ -f "$input" ] || die "input is not a regular file: $input"
[ -x "$input" ] || die "input is not executable: $input"
[ ! -e "$output" ] || die "output already exists: $output"

input_dir=$(dirname "$input")
input_name=$(basename "$input")
input_dir=$(CDPATH= cd -P -- "$input_dir" && pwd) ||
    die "could not resolve input directory: $input_dir"
input="$input_dir/$input_name"

output_dir=$(dirname "$output")
mkdir -p "$output_dir"
output_name=$(basename "$output")
output_dir=$(CDPATH= cd -P -- "$output_dir" && pwd) ||
    die "could not resolve output directory: $output_dir"
output="$output_dir/$output_name"
[ ! -e "$output" ] || die "output already exists: $output"

case "$(uname -m)" in
    arm64|x86_64) native_arch=$(uname -m) ;;
    *) die "unsupported macOS runner architecture: $(uname -m)" ;;
esac
input_archs=$(lipo -archs "$input") ||
    die "could not inspect input Mach-O architectures: $input"
case " $input_archs " in
    *" $native_arch "*) ;;
    *) die "input is not a native $native_arch Mach-O executable: $input" ;;
esac
strings "$input" | grep -Fq '/explorer/favicon.svg' ||
    die "input does not contain the embedded Explorer assets; build with default features"

script_dir=$(CDPATH= cd -P -- "$(dirname "$0")" && pwd) ||
    die "could not resolve script directory"
inspect_script="$script_dir/inspect-pkg.sh"
[ -x "$inspect_script" ] || die "package inspector is not executable: $inspect_script"

work_base="$output_dir/.${output_name}.work.$$"
counter=0
while :; do
    work_dir="${work_base}.${counter}"
    if mkdir -m 700 "$work_dir" 2>/dev/null; then
        break
    fi
    counter=$((counter + 1))
    [ "$counter" -lt 100 ] || die "could not create a private staging directory"
done

cleanup() {
    if [ -n "${work_dir:-}" ] && [ -d "$work_dir" ]; then
        rm -rf -- "$work_dir"
    fi
}
trap cleanup EXIT
trap 'exit 128' HUP INT TERM

payload="$work_dir/payload"
mkdir -p "$payload/usr/local/bin"
install -m 0755 "$input" "$payload/usr/local/bin/cosmos-emulator"
[ -x "$payload/usr/local/bin/cosmos-emulator" ] ||
    die "staged binary lost its executable mode"

set -- \
    --root "$payload" \
    --identifier "com.azure-light-cosmos-emulator.cosmos-emulator" \
    --version "$version" \
    --install-location "/" \
    --ownership recommended

if [ -n "$identity" ]; then
    set -- "$@" --sign "$identity"
fi

built_package="$work_dir/cosmos-emulator.pkg"
pkgbuild "$@" "$built_package"

if [ -n "$identity" ]; then
    "$inspect_script" --require-signed "$built_package"
else
    "$inspect_script" --require-unsigned "$built_package"
fi

[ ! -e "$output" ] || die "output was created while the package was being built: $output"
mv "$built_package" "$output"

printf 'Created %s (%s)\n' "$output" "$(
    if [ -n "$identity" ]; then
        printf 'signed'
    else
        printf 'unsigned'
    fi
)"
