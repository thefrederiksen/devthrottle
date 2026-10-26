#!/usr/bin/env bash
# Resolve a hosted Gateway image in the registry to an immutable digest and the commit it was built from.
#
# The ONE place this resolution lives. Two callers use it:
#   * .github/workflows/rollback-hosted-gateway.yml, step "Find the image for that commit" - the manual
#     emergency lever, with whatever the person typed.
#   * scripts/rollback-to-last-known-good.sh - the automatic rollback inside a failed deploy (issue #4514),
#     always with the words last-known-good.
# They used to be one block of shell inside the rollback workflow. The automatic rollback cannot dispatch
# that workflow - it runs inside the deploy job, which already holds the hosted-gateway-production
# concurrency group, so a dispatched rollback would wait for the deploy that is waiting for it - so the
# block was moved here and both call it, rather than a second copy drifting away from the first.
#
#   scripts/resolve-gateway-image.sh <what>
#
# <what> takes three forms, because the registry keeps only three commits by name:
#   last-known-good  the moving tag the deploy puts on each image that passed its whole deploy
#   sha256:...       a raw digest, the way back to an image whose tags have all been purged
#   anything else    a short commit, resolved as ci-<commit>
#
# On success it prints exactly two lines to standard output, ready to append to GITHUB_OUTPUT:
#   digest=sha256:...
#   commit=<short commit, or empty when the image carries no ci-<commit> tag>
# Everything meant for a person goes to standard error.
#
# Needs: az (logged in), and the ACR and IMAGE_NAME environment variables.
# The proof is scripts/rollback-to-last-known-good.test.sh, which runs it against a fake az.
#
# Exit codes: 0 resolved; 1 no such image in the registry; 2 bad usage.

set -uo pipefail

WHAT=${1:-}
if [ -z "$WHAT" ] || [ -z "${ACR:-}" ] || [ -z "${IMAGE_NAME:-}" ]; then
  echo "usage: ACR=<registry> IMAGE_NAME=<repository> $0 <short commit | last-known-good | sha256:...>" >&2
  exit 2
fi

case "$WHAT" in
  last-known-good) TAG="last-known-good" ;;
  sha256:*)        TAG="$WHAT" ;;
  *)               TAG="ci-$WHAT" ;;
esac
case "$TAG" in
  sha256:*) REF_ARG="${IMAGE_NAME}@${TAG}" ;;
  *)        REF_ARG="${IMAGE_NAME}:${TAG}" ;;
esac

if ! DIGEST=$(az acr repository show --name "$ACR" --image "$REF_ARG" \
                --query digest --output tsv 2>/dev/null) || [ -z "$DIGEST" ]; then
  {
    echo "ERROR: no image '$REF_ARG' in registry $ACR."
    echo "       Production has NOT been changed by this resolution."
    echo ""
    echo "       The registry keeps the three newest deploys by name, plus 'live' (what production"
    echo "       is running) and 'last-known-good'. Anything older has been purged. If you need an"
    echo "       image older than that, its digest is in its own deploy run's log and can be passed"
    echo "       as sha256:... - but only if the manifest still exists."
    echo ""
    echo "       Tags that do exist, most recent first:"
    az acr repository show-tags --name "$ACR" --repository "$IMAGE_NAME" \
      --orderby time_desc --top 20 --output tsv 2>/dev/null | sed 's/^/         /'
  } >&2
  exit 1
fi
case "$DIGEST" in
  sha256:*) ;;
  *) echo "ERROR: the registry answered '$DIGEST' for '$REF_ARG', which is not a sha256 digest." >&2; exit 1 ;;
esac

# Which commit does that image carry? For a ci-<commit> tag it is in the name; for a moving tag or a
# digest it is not, and callers check production by commit, so read it off the image's OWN tags.
# `acr repository show-manifests` reads the same thing and is DEPRECATED; `acr manifest list-metadata`
# is the replacement, and both were verified against this registry on 2026-09-20.
if [ "$TAG" = "ci-$WHAT" ]; then
  COMMIT="$WHAT"
else
  # ITS ABSENCE MUST NOT REFUSE THE RESOLUTION (review finding). The purge keeps only the three newest
  # ci- tags, so the last known good loses its ci- tag within three deploys while the image itself stays
  # pullable on the moving tag. Refusing then would disarm the emergency lever at exactly the moment it
  # is needed. Without a commit a caller can verify that production comes back HEALTHY but not WHICH
  # code it is running, and each caller says so.
  COMMIT=$(az acr manifest list-metadata --registry "$ACR" --name "$IMAGE_NAME" \
             --query "[?digest=='${DIGEST}'].tags[]" -o tsv 2>/dev/null \
           | grep '^ci-' | head -n1 | sed 's/^ci-//' || true)
  if [ -z "$COMMIT" ]; then
    echo "NOTE: ${TAG} carries no ci-<commit> tag, so production can be checked for coming back" >&2
    echo "      HEALTHY on it, not for which commit it is running." >&2
  fi
fi

echo "Resolved ${TAG} = commit ${COMMIT:-<unknown>} (${DIGEST})." >&2
echo "digest=$DIGEST"
echo "commit=$COMMIT"
