#!/usr/bin/env bash
#
# Takes one release download, unpacks it the way its user would, and makes a real backup with it.
#
#   try-download.sh <archive> <version it must report> [command to run the program under]
#
# The tests prove hback's behaviour from a build tree. This proves something they cannot: that the
# file people download starts on this system at all. A build that lost its SQLite library, or an
# archive that lost the program's executable bit, passes every test and fails here.
#
# The optional third argument runs the program under another command, such as "arch -x86_64" for
# the Intel build on an Apple silicon Mac.

set -euo pipefail

archive=$1
expected=$2
under=${3:-}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# On Windows this script runs in Git Bash, where a path looks like /tmp/x. hback is a Windows
# program there and needs the path as Windows writes it.
native() {
  if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

fail() {
  echo "FAILED: $*" >&2
  exit 1
}

echo "== $(basename "$archive")"

mkdir "$work/install"
case "$archive" in
  *.zip) unzip -q "$archive" -d "$work/install" ;;
  *) tar -xzf "$archive" -C "$work/install" ;;
esac

# Every download unpacks to one folder named hback, with the licence and the readme in it.
home="$work/install/hback"
for file in hback.sample.xml README.md LICENSE; do
  [ -f "$home/$file" ] || fail "$file is not in the download"
done

if [ -f "$home/hback.exe" ]; then
  program=("$home/hback.exe")
elif [ -f "$home/hback" ]; then
  [ -x "$home/hback" ] || fail "hback is not marked as a program that can be run"
  program=("$home/hback")
elif [ -f "$home/hback.dll" ]; then
  program=(dotnet "$home/hback.dll")
else
  fail "there is no hback program in the download"
fi
# shellcheck disable=SC2206  # the third argument is a command with its arguments, split on purpose
[ -z "$under" ] || program=($under "${program[@]}")

reported=$("${program[@]}" version | tr -d '\r')
[ "$reported" = "hback $expected" ] || fail "it reports '$reported', and should report 'hback $expected'"
echo "$reported"

mkdir -p "$work/source/notes" "$work/destination"
echo "the first file" > "$work/source/hello.txt"
echo "the second file" > "$work/source/notes/todo.txt"

# The configuration goes beside the program, where a new user is told to put it.
cat > "$home/hback.xml" << EOF
<hback>
  <backup>
    <name>try</name>
    <sources><![CDATA[
      $(native "$work/source")
    ]]></sources>
    <destination><![CDATA[$(native "$work/destination")]]></destination>
  </backup>
</hback>
EOF

"${program[@]}" run --dry-run
[ ! -e "$work/destination/current" ] || fail "a dry run wrote to the destination"

"${program[@]}" run
for name in hello.txt todo.txt; do
  copy=$(find "$work/destination/current" -name "$name")
  [ -n "$copy" ] || fail "$name is not in the backup"
  cmp -s "$copy" "$(find "$work/source" -name "$name")" || fail "$name in the backup differs from the original"
done

"${program[@]}" status
"${program[@]}" verify

"${program[@]}" restore try --to "$(native "$work/restored")"
restored=$(find "$work/restored" -name todo.txt)
[ -n "$restored" ] || fail "todo.txt was not restored"
cmp -s "$restored" "$work/source/notes/todo.txt" || fail "the restored todo.txt differs from the original"

echo "== $(basename "$archive") works"
