#!/usr/bin/env bash
# 把安卓安装包上传成一条 GitHub Release（含附件）。
#
# 用法（在仓库根目录，Git Bash 里跑）：
#   bash tools/release/upload_release.sh
#
# 令牌从哪来（按顺序找）：
#   1. 环境变量 GH_TOKEN 或 GITHUB_TOKEN
#   2. 你电脑上 git 已经存好的 GitHub 凭据（Windows 凭据管理器里的那份）
# 令牌不会被打印，也不会写进任何文件。
#
# 想改哪一项就设对应的环境变量，例如：
#   TAG=v0.1.1 APK="D:/别的目录/xxx.apk" bash tools/release/upload_release.sh

set -euo pipefail

REPO="${REPO:-charlotte114/inotia4}"
TAG="${TAG:-v0.1.0}"
BRANCH="${BRANCH:-main}"
APK="${APK:-D:/Desktop/inoita4/godot/build/inoita4.apk}"
NOTES="${NOTES:-$(dirname "$0")/$TAG.md}"
ASSET_NAME="$(basename "$APK")"
RELEASE_TITLE="${RELEASE_TITLE:-$TAG — 首个能在手机上玩的版本}"

API="https://api.github.com/repos/$REPO"
UPLOADS="https://uploads.github.com/repos/$REPO"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

die() { printf '\n[失败] %s\n' "$1" >&2; exit 1; }
step() { printf '\n==> %s\n' "$1"; }

# ---------- 前置检查 ----------
[ -f "$APK" ] || die "找不到安装包：$APK"
[ -f "$NOTES" ] || die "找不到发布说明：$NOTES"

APK_SIZE=$(wc -c < "$APK" | tr -d ' ')
printf '仓库   : %s\n标签   : %s\n安装包 : %s (%.1f MB)\n说明   : %s\n' \
  "$REPO" "$TAG" "$APK" "$(awk "BEGIN{print $APK_SIZE/1048576}")" "$NOTES"

# ---------- 取令牌 ----------
TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
if [ -z "$TOKEN" ]; then
  step "没给环境变量，尝试用本机 git 已保存的 GitHub 凭据"
  TOKEN=$(printf 'protocol=https\nhost=github.com\n\n' | git credential fill 2>/dev/null | sed -n 's/^password=//p' || true)
fi
[ -n "$TOKEN" ] || die "没有可用的令牌。请设置 GH_TOKEN，或先确认 git push 时能自动登录同一仓库。"

AUTH=(-H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28")

# ---------- 把文本转成 JSON 字符串（不依赖 jq / python）----------
json_str() { sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\t/\\t/g' | tr -d '\r'; }
json_file_body() {
  sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\t/\\t/g' -e 's/\r//' "$1" \
    | awk 'BEGIN{ORS=""} {if (NR>1) print "\\n"; print}'
}

# ---------- 建 release（已有就复用）----------
TITLE_JSON=$(printf '%s' "$RELEASE_TITLE" | json_str)
BODY_JSON=$(json_file_body "$NOTES")
cat > "$TMP/payload.json" <<EOF
{"tag_name":"$TAG","target_commitish":"$BRANCH","name":"$TITLE_JSON","body":"$BODY_JSON","draft":false,"prerelease":true}
EOF

step "查一下 $TAG 是不是已经有 Release"
HTTP=$(curl -s -o "$TMP/get.json" -w '%{http_code}' "${AUTH[@]}" "$API/releases/tags/$TAG")
if [ "$HTTP" = "200" ]; then
  REL_ID=$(grep -o '"id":[0-9]*' "$TMP/get.json" | head -1 | cut -d: -f2)
  echo "已存在，复用 Release #$REL_ID"
elif [ "$HTTP" = "404" ]; then
  step "创建 Release $TAG"
  HTTP=$(curl -s -X POST -o "$TMP/create.json" -w '%{http_code}' "${AUTH[@]}" \
    -H 'Content-Type: application/json' --data-binary "@$TMP/payload.json" "$API/releases")
  [ "$HTTP" = "201" ] || { cat "$TMP/create.json"; die "创建 Release 失败（HTTP $HTTP）"; }
  REL_ID=$(grep -o '"id":[0-9]*' "$TMP/create.json" | head -1 | cut -d: -f2)
  echo "已创建 Release #$REL_ID"
else
  cat "$TMP/get.json"; die "查询 Release 失败（HTTP $HTTP）"
fi

# ---------- 同名附件先删掉，避免 422 ----------
step "检查同名附件"
curl -s -o "$TMP/assets.json" "${AUTH[@]}" "$API/releases/$REL_ID/assets"
OLD_ID=$(tr '{' '\n' < "$TMP/assets.json" | grep -F "\"name\":\"$ASSET_NAME\"" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2 || true)
if [ -n "${OLD_ID:-}" ]; then
  echo "附件 $ASSET_NAME 已存在（#$OLD_ID），先删掉再传"
  curl -s -X DELETE -o /dev/null -w '删除返回 HTTP %{http_code}\n' "${AUTH[@]}" "$API/assets/$OLD_ID"
fi

# ---------- 上传 ----------
step "上传 $ASSET_NAME（$(awk "BEGIN{printf \"%.1f\", $APK_SIZE/1048576}") MB，别中途断网）"
HTTP=$(curl -# -X POST -o "$TMP/up.json" -w '%{http_code}' "${AUTH[@]}" \
  -H "Content-Type: application/vnd.android.package-archive" \
  --data-binary "@$APK" "$UPLOADS/releases/$REL_ID/assets?name=$ASSET_NAME" || echo 000)
[ "$HTTP" = "201" ] || { cat "$TMP/up.json" 2>/dev/null; die "上传失败（HTTP $HTTP）"; }

# ---------- 核对服务端大小 ----------
step "核对服务端记录的大小"
curl -s -o "$TMP/final.json" "${AUTH[@]}" "$API/releases/$REL_ID/assets"
SERVER_SIZE=$(tr '{' '\n' < "$TMP/final.json" | grep -F "\"name\":\"$ASSET_NAME\"" | grep -o '"size":[0-9]*' | head -1 | cut -d: -f2 || true)
URL=$(grep -o 'https://github.com/[^"]*releases/download/[^"]*' "$TMP/final.json" | head -1 || true)

printf '\n本地 %s 字节 / 服务端 %s 字节\n' "$APK_SIZE" "${SERVER_SIZE:-未知}"
[ "${SERVER_SIZE:-0}" = "$APK_SIZE" ] || die "大小对不上，附件可能没传完，请重跑一次。"
printf '\n完成。Release 页面：https://github.com/%s/releases/tag/%s\n' "$REPO" "$TAG"
[ -n "$URL" ] && printf '下载直链：%s\n' "$URL"
