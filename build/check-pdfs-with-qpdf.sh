#!/usr/bin/env bash
# Runs "qpdf --check" on every PDF in a folder (CI only: qpdf is an independent PDF reader, installed on the runner).
# Fails when the folder holds no PDF, or qpdf reports an error (exit code 2). Warnings (exit code 3) are shown in the
# job log as GitHub warnings and do not fail the run.
set -u
folder=${1:?usage: check-pdfs-with-qpdf.sh <folder>}
command -v qpdf >/dev/null || { echo "::error::qpdf was not found; install it first (brew install qpdf)."; exit 1; }
qpdf --version | head -n 1
errors=0; warnings=0; count=0
while IFS= read -r -d '' file; do
    count=$((count + 1))
    echo "--- qpdf --check $(basename "$file")"
    qpdf --check "$file"; code=$?
    if [[ $code -eq 2 ]]; then echo "::error::qpdf --check reported errors in $(basename "$file")"; errors=$((errors + 1))
    elif [[ $code -eq 3 ]]; then echo "::warning::qpdf --check reported warnings for $(basename "$file")"; warnings=$((warnings + 1))
    elif [[ $code -ne 0 ]]; then echo "::error::qpdf exited with $code for $(basename "$file")"; errors=$((errors + 1)); fi
done < <(find "$folder" -type f -name '*.pdf' -print0)
if [[ $count -eq 0 ]]; then echo "::error::No PDF files in $folder to check"; exit 1; fi
echo "qpdf checked $count PDF(s): $errors with errors, $warnings with warnings only."
[[ $errors -eq 0 ]]
