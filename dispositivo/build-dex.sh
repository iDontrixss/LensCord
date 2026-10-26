#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"      # .../Lenscord/dispositivo
proj="$(dirname "$root")"                                  # .../Lenscord
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$proj/tools/android-sdk}}"
[ -d "$sdk/platforms/android-35" ] || sdk="$proj/tools/android-sdk"
android_jar="$sdk/platforms/android-35/android.jar"
d8="$sdk/build-tools/34.0.0/d8"
src="$root/src"
build="$root/build"
out_dex="$root/out/lenscord.dex"

if [ -n "${JAVA_HOME:-}" ] && [ -x "$JAVA_HOME/bin/javac" ]; then
  javac="$JAVA_HOME/bin/javac"
elif command -v javac >/dev/null 2>&1; then
  javac="$(command -v javac)"
elif [ -x /usr/libexec/java_home ] && jh="$(/usr/libexec/java_home 2>/dev/null)" && [ -x "$jh/bin/javac" ]; then
  javac="$jh/bin/javac"
else
  echo "No se encontro un JDK (javac). d8 requiere Java 11 o superior." >&2
  exit 1
fi
echo "javac: $javac"

for f in "$android_jar" "$d8"; do
  [ -e "$f" ] || { echo "Falta $f (descarga el SDK a tools/android-sdk)" >&2; exit 1; }
done

rm -rf "$build" "$root/out/dexdir"
mkdir -p "$build" "$root/out/dexdir"

echo "javac -> .class"
mapfile -t fuentes < <(find "$src" -name '*.java')
"$javac" -source 8 -target 8 -nowarn \
  -bootclasspath "$android_jar" -classpath "$android_jar" \
  -d "$build" "${fuentes[@]}"

echo "d8 -> .dex"
mapfile -t clases < <(find "$build" -name '*.class')
[ "${#clases[@]}" -gt 0 ] || { echo "javac no produjo ningun .class" >&2; exit 1; }
"$d8" --release --min-api 26 --lib "$android_jar" --output "$root/out/dexdir" "${clases[@]}"

rm -f "$out_dex"
mv "$root/out/dexdir/classes.dex" "$out_dex"
rm -rf "$root/out/dexdir"
echo "OK  $out_dex  ($(( $(wc -c < "$out_dex") / 1024 )) KB, ${#clases[@]} clases)"
