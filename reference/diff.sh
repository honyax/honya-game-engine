#!/usr/bin/env bash
#
# reference/DayXX と work/ の写経コードを比べるスクリプト(WSL / bash 用)。
#
#   ./diff.sh Day15/Shader.cs        1ファイルの差分を表示する
#   ./diff.sh Day15                  そのDayの全ファイルを比べ、差分のあるものを列挙する
#   ./diff.sh Day25/Physics          サブフォルダだけに絞ることもできる
#
# work 側は「フォルダ構成が reference と違ってもよい」ようにファイル名で探しに行く。
# reference が shaders/ や Physics/ に分かれても、呼び出し方は変わらない。
#
# csproj だけは特別扱いで、コメントと <RootNamespace> を落としてから比べる
# (reference は DayXX.csproj、work は HonyaEngine.csproj というように名前も違うので、
#  拡張子で対応付ける)。

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if [ -t 1 ]; then
    C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YEL=$'\033[33m'
    C_CYA=$'\033[36m'; C_DIM=$'\033[2m';  C_OFF=$'\033[0m'
else
    C_RED=''; C_GRN=''; C_YEL=''; C_CYA=''; C_DIM=''; C_OFF=''
fi

usage() {
    cat <<'USAGE'
使い方:
  ./diff.sh DayXX/ファイル名     1ファイルの差分を表示
  ./diff.sh DayXX               そのDayの全テキストファイルを比較し、結果を分類して表示
  ./diff.sh DayXX/サブフォルダ   サブフォルダ以下だけを比較

例:
  ./diff.sh Day15/Shader.cs
  ./diff.sh Day15/shaders/textured.vert
  ./diff.sh Day15/Day15.csproj
  ./diff.sh Day15
  ./diff.sh Day25/Physics

差分は前後の文脈行を出さない。文脈が欲しいときは:
  DIFF_CONTEXT=3 ./diff.sh Day15/Shader.cs

終了コード: 0 = 差分なし / 1 = 差分あり / 2 = 使い方の誤り
USAGE
}

# Day番号から写経先のプロジェクトを決める(CLAUDE.md の「5系統」に対応)。
work_dir_for_day() {
    local n=$((10#$1))
    if   [ "$n" -le 1  ]; then echo "work/Framebuffer"
    elif [ "$n" -le 10 ]; then echo "work/SoftwareRasterizer"
    elif [ "$n" -le 13 ]; then echo "work/RawGL"
    else                       echo "work/HonyaEngine/HonyaEngine"
    fi
}

# バイナリ(png 等)を比較対象から外す。空ファイルはテキスト扱い。
is_text() {
    [ -s "$1" ] || return 0
    LC_ALL=C grep -qI -e '' -- "$1"
}

# csproj の正規化。XMLコメントを落とし、行頭行末の空白と空行を潰し、
# <RootNamespace> は無視する(work 側はフォルダ名から決まるので不要)。
normalize_csproj() {
    awk '
    {
        s = $0; out = ""
        while (1) {
            if (inc) {
                p = index(s, "-->")
                if (p == 0) { s = ""; break }
                inc = 0; s = substr(s, p + 3); continue
            }
            p = index(s, "<!--")
            if (p == 0) { out = out s; break }
            out = out substr(s, 1, p - 1); s = substr(s, p + 4); inc = 1
        }
        print out
    }' "$1" \
    | sed -e 's/\r$//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
    | grep -v '^$' \
    | grep -v '^<RootNamespace>.*</RootNamespace>$'
}

# work 側から対応するファイルの候補を探して出力する(0件 / 1件 / 複数)。
# 呼び出し側は行数を見て「無い・確定・判定不能」を判断する。
find_work_file() {
    local wd="$1" rel="$2" base pattern
    base="$(basename "$rel")"
    if [[ "$base" == *.csproj ]]; then pattern='*.csproj'; else pattern="$base"; fi

    local -a hits=()
    mapfile -t hits < <(find "$wd" -type f -name "$pattern" \
        -not -path '*/bin/*' -not -path '*/obj/*' | sort)

    (( ${#hits[@]} == 0 )) && return 0
    if (( ${#hits[@]} > 1 )); then
        # 同名が複数あるときの優先順位:
        #   1. reference 側と相対パスがそっくり同じもの
        #   2. 相対パスの末尾が一致するもの(work 側が1段深いところに置いている場合)
        # どちらも決まらなければ候補を全部返し、呼び出し側で「判定不能」にする
        local h
        for h in "${hits[@]}"; do
            if [[ "${h#$wd/}" == "$rel" ]]; then printf '%s\n' "$h"; return 0; fi
        done
        for h in "${hits[@]}"; do
            if [[ "$h" == */"$rel" ]]; then printf '%s\n' "$h"; return 0; fi
        done
    fi
    printf '%s\n' "${hits[@]}"
}

# 差分があれば 0 を返す(名前のとおりの真偽)。
files_differ() {
    local ref="$1" wrk="$2"
    if [[ "$ref" == *.csproj ]]; then
        ! diff -q <(normalize_csproj "$ref") <(normalize_csproj "$wrk") >/dev/null 2>&1
    else
        ! diff -q --strip-trailing-cr "$ref" "$wrk" >/dev/null 2>&1
    fi
}

show_diff() {
    local ref="$1" wrk="$2"
    # 写経の差分確認では前後の文脈行がノイズになるので、既定は文脈0行。
    # 前後を見たいときは DIFF_CONTEXT=3 ./diff.sh ... のように指定する。
    local -a opt=(-U "${DIFF_CONTEXT:-0}" --strip-trailing-cr)
    diff --color=auto /dev/null /dev/null >/dev/null 2>&1 && opt+=(--color=auto)

    local rl="reference: ${ref#$ROOT/}" wl="work:      ${wrk#$ROOT/}"
    if [[ "$ref" == *.csproj ]]; then
        echo "${C_DIM}(csproj はコメントと <RootNamespace> を無視して比較)${C_OFF}"
        diff "${opt[@]}" --label "$rl" --label "$wl" \
            <(normalize_csproj "$ref") <(normalize_csproj "$wrk")
    else
        diff "${opt[@]}" --label "$rl" --label "$wl" "$ref" "$wrk"
    fi
}

# ---- 引数の解釈 ----

case "${1:-}" in
    ''|-h|--help) usage; exit 2 ;;
esac

target="${1#./}"; target="${target#reference/}"; target="${target%/}"
day="${target%%/*}"
rest="${target#"$day"}"; rest="${rest#/}"

# day15 や Day5 のような書き方も受け付けて DayXX に正規化する
if [[ "$day" =~ ^[Dd]ay0*([0-9]{1,2})$ ]]; then
    day="$(printf 'Day%02d' "$((10#${BASH_REMATCH[1]}))")"
else
    echo "${C_RED}エラー${C_OFF}: Day番号の指定が不正です: '$day'(例: Day15)" >&2
    exit 2
fi

ref_day="$ROOT/reference/$day"
work_rel="$(work_dir_for_day "${day#Day}")"
work_dir="$ROOT/$work_rel"

[ -d "$ref_day" ] || { echo "${C_RED}エラー${C_OFF}: $ref_day がありません" >&2; exit 2; }
[ -d "$work_dir" ] || { echo "${C_RED}エラー${C_OFF}: $work_rel がありません" >&2; exit 2; }

# ---- ファイル1つを指定したとき ----

if [ -n "$rest" ] && [ -f "$ref_day/$rest" ]; then
    ref_file="$ref_day/$rest"
    found=()
    mapfile -t found < <(find_work_file "$work_dir" "$rest")

    if (( ${#found[@]} == 0 )); then
        echo "${C_YEL}work 側に見つかりません${C_OFF}: $(basename "$rest") ($work_rel 以下)" >&2
        echo "${C_DIM}まだ写経していないファイルかもしれません。${C_OFF}" >&2
        exit 1
    elif (( ${#found[@]} > 1 )); then
        echo "${C_YEL}同名のファイルが複数あります。${C_OFF}どれか1つを直接 diff してください:" >&2
        printf '  %s\n' "${found[@]#$ROOT/}" >&2
        exit 2
    fi

    work_file="${found[0]}"
    if files_differ "$ref_file" "$work_file"; then
        show_diff "$ref_file" "$work_file"
        exit 1
    else
        echo "${C_GRN}一致${C_OFF}: ${ref_file#$ROOT/}  ==  ${work_file#$ROOT/}"
        exit 0
    fi
fi

# ---- Day(またはサブフォルダ)を指定したとき ----

scan_root="$ref_day${rest:+/$rest}"
if [ ! -d "$scan_root" ]; then
    echo "${C_RED}エラー${C_OFF}: reference/$day/$rest がありません" >&2
    exit 2
fi

declare -a same=() diff_list=() missing=() ambiguous=()

while IFS= read -r f; do
    is_text "$f" || continue
    rel="${f#$ref_day/}"
    found=()
    mapfile -t found < <(find_work_file "$work_dir" "$rel")
    if   (( ${#found[@]} == 0 )); then missing+=("$rel")
    elif (( ${#found[@]} > 1 ));  then ambiguous+=("$rel")
    elif files_differ "$f" "${found[0]}"; then diff_list+=("$rel")
    else same+=("$rel")
    fi
done < <(find "$scan_root" -type f -not -path '*/bin/*' -not -path '*/obj/*' | sort)

# work 側にしか無いファイル(前Dayで消えたシェーダの残りなど)も拾っておく。
# サブフォルダに絞ったときは範囲外の話になるので調べない。
declare -a extra=()
[ -n "$rest" ] || while IFS= read -r wf; do
    is_text "$wf" || continue
    base="$(basename "$wf")"
    [[ "$base" == *.csproj ]] && continue
    if ! find "$ref_day" -type f -name "$base" \
            -not -path '*/bin/*' -not -path '*/obj/*' | grep -q .; then
        extra+=("${wf#$work_dir/}")
    fi
done < <(find "$work_dir" -type f -not -path '*/bin/*' -not -path '*/obj/*' | sort)

echo "${C_CYA}reference/$day${rest:+/$rest}${C_OFF}  <->  ${C_CYA}$work_rel${C_OFF}"
echo

print_group() {
    local color="$1" title="$2"; shift 2
    (( $# == 0 )) && return
    echo "${color}${title} ($#)${C_OFF}"
    printf '  %s\n' "$@"
    echo
}

print_group "$C_RED" "差分あり"          "${diff_list[@]}"
print_group "$C_YEL" "work 側に無い"      "${missing[@]}"
print_group "$C_YEL" "同名が複数あり判定不能" "${ambiguous[@]}"
print_group "$C_DIM" "reference に無い"   "${extra[@]}"

echo "${C_GRN}一致 (${#same[@]})${C_OFF}"

if (( ${#diff_list[@]} > 0 )); then
    echo
    echo "${C_DIM}中身を見る: ./diff.sh $day/${diff_list[0]}${C_OFF}"
fi

(( ${#diff_list[@]} + ${#missing[@]} + ${#ambiguous[@]} > 0 )) && exit 1
exit 0
