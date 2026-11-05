#!/usr/bin/env bash
# Runs "qpdf --check" on every PDF in a folder (CI only: qpdf is an independent PDF reader, installed on the runner).
# Fails when the folder holds no PDF, or qpdf exits with anything but 0: errors (2) and warnings (3, a repair such as a rebuilt
# cross-reference table) both fail the run.
set -u
folder=${1:?usage: check-pdfs-with-qpdf.sh <folder>}
command -v qpdf >/dev/null || { echo "::error::qpdf was not found; install it first (brew install qpdf)."; exit 1; }
qpdf --version | head -n 1
errors=0; count=0
while IFS= read -r -d '' file; do
    count=$((count + 1))
    echo "--- qpdf --check $(basename "$file")"
    qpdf --check "$file"; code=$?
    # Any exit code other than 0 fails: 3 means qpdf repaired or recovered something (e.g. a rebuilt cross-reference table).
    if [[ $code -ne 0 ]]; then echo "::error::qpdf --check exited with $code (2 = errors, 3 = warnings) for $(basename "$file")"; errors=$((errors + 1)); fi
done < <(find "$folder" -type f -name '*.pdf' -print0)
if [[ $count -eq 0 ]]; then echo "::error::No PDF files in $folder to check"; exit 1; fi
echo "qpdf checked $count PDF(s): $errors failed."
[[ $errors -eq 0 ]]
