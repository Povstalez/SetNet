#!/usr/bin/env bash
# Assembles the DocFX conceptual content from the repo's existing docs + every module README, then generates the site.
# Run from anywhere; it resolves the repo root. The generated dirs (docfx/api, docfx/articles, docfx/modules, _site)
# are git-ignored — this script (and the CI workflow) reproduces them, so docs live in ONE place (the repo markdown).
set -euo pipefail
cd "$(dirname "$0")/.."          # repo root
export PATH="$HOME/.dotnet/tools:$PATH"

rm -rf docfx/articles docfx/modules
mkdir -p docfx/articles docfx/modules

# ---- Guide (a few stable overview docs) ----
cp docs/GUIDE.en.md        docfx/articles/guide.md
cp docs/COMMUNICATION.md   docfx/articles/communication.md
cp docs/PERFORMANCE.en.md  docfx/articles/performance.md
cat > docfx/articles/toc.yml <<'EOF'
- name: User Guide
  href: guide.md
- name: Communication model
  href: communication.md
- name: Performance
  href: performance.md
EOF

# ---- Modules (the catalog + every module's README, rendered as pages) ----
cp docs/MODULES.md docfx/modules/index.md
{
  echo "- name: Catalog"
  echo "  href: index.md"
} > docfx/modules/toc.yml
while IFS= read -r readme; do
  name="$(printf '%s' "$readme" | sed -E 's|.*/(SetNet[^/]*)/README.md|\1|')"
  cp "$readme" "docfx/modules/$name.md"
  printf -- '- name: %s\n  href: %s.md\n' "$name" "$name" >> docfx/modules/toc.yml
done < <(find src -name README.md -path '*/SetNet.*' | sort)

# ---- Generate API reference + build the static site ----
docfx metadata docfx.json
cat > docfx/api/index.md <<'EOF'
# API Reference

The complete, generated reference for **every public type, property, method and option** across all SetNet packages —
sourced directly from the inline XML documentation. Browse by namespace in the left navigation, or search (top bar).
Every member carries its summary, so this is where you find *what a class does* and *what each field means*.
EOF
docfx build docfx.json
echo "setnet.lemeshev.dev" > _site/CNAME     # GitHub Pages custom domain
echo "Site built → _site/"
