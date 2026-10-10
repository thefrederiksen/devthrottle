"""How cc-secrets reaches the Gateway for a secret transfer (the Secret Handoff mission).

Two credentials, the first that applies, and no other:

  1. inside a DevThrottle session: the session's own key (CC_GATEWAY_URL + CC_GATEWAY_SESSION_KEY), limited by the
     Gateway's session key guard to the routes an agent may call;
  2. outside a session - the owner's own terminal, or the window opened from the Start menu: this machine's own
     Gateway credential (`gateway.url` and `gateway.token` in config.json), the one the Director uses. This is the
     order the tool error reporter uses by the owner's ruling of 9 October 2026, and it is resolved by the same code
     (cc_shared.tool_errors.resolve_credential).

A machine that is not signed in to a Gateway has neither, and a transfer is refused with that reason - there is no
third way. Nothing sent through here ever carries a secret or an envelope: the routes take entry names, machine names,
reasons and answers, and an envelope only ever travels down a Director's own tunnel.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Optional

from .errors import CcSecretsError

KIND_SESSION = "session"
KIND_MACHINE = "machine"


@dataclass(frozen=True)
class Link:
    kind: str
    url: str
    bearer: str


def resolve() -> Link:
    from cc_shared.tool_errors import resolve_credential

    try:
        credential = resolve_credential()
    except ValueError as exc:
        raise CcSecretsError(f"This machine's Gateway settings cannot be read: {exc}") from exc
    if credential.kind not in (KIND_SESSION, KIND_MACHINE) or not credential.bearer:
        raise CcSecretsError("This machine is not signed in to a Gateway, so a secret cannot be moved to or from it. "
                             "Sign the Director on this machine in to your account first.")
    return Link(credential.kind, credential.url, credential.bearer)


def get(path: str, link: Optional[Link] = None) -> Any:
    from cc_shared import gateway

    link = link or resolve()
    try:
        return gateway.get_json(path, bearer=link.bearer, base_url=link.url)
    except gateway.GatewayError as exc:
        raise CcSecretsError(str(exc)) from exc


def post(path: str, body: dict, link: Optional[Link] = None) -> Any:
    from cc_shared import gateway

    link = link or resolve()
    try:
        return gateway.post_json(path, body, bearer=link.bearer, base_url=link.url)
    except gateway.GatewayError as exc:
        raise CcSecretsError(str(exc)) from exc
