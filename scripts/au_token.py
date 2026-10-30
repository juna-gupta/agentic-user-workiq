"""Mint agentic-user tokens for local setup checks."""

from __future__ import annotations

import argparse
import base64
import json
from pathlib import Path

from graph_auth import HERE

STATE = HERE / "au-state.json"
SECRET = HERE / ".blueprint-secret.json"
EXCHANGE_SCOPE = "api://AzureADTokenExchange/.default"
JWT_BEARER = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
WORKIQ_SCOPE = "api://workiq.svc.cloud.microsoft/.default"


def load_json(path: Path) -> dict:
    if not path.exists():
        raise SystemExit(f"Missing {path}. Run create_au.py first.")
    return json.loads(path.read_text(encoding="utf-8"))


def _post(tenant_id: str, data: dict[str, str]) -> str:
    import requests

    response = requests.post(f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token", data=data, timeout=30)
    if not response.ok:
        raise RuntimeError(f"{data.get('grant_type')} {data.get('client_id')}: {response.status_code} {response.text[:600]}")
    return response.json()["access_token"]


def agent_user_token(scope: str = WORKIQ_SCOPE, *, state_path: Path = STATE, secret_path: Path = SECRET) -> str:
    state = load_json(state_path)
    secret = load_json(secret_path)["secretText"]
    identity = state["agentIdentityId"]
    t1 = _post(
        state["tenantId"],
        {
            "client_id": state["blueprintAppId"],
            "client_secret": secret,
            "grant_type": "client_credentials",
            "scope": EXCHANGE_SCOPE,
            "fmi_path": identity,
        },
    )
    t2 = _post(
        state["tenantId"],
        {
            "client_id": identity,
            "client_assertion_type": JWT_BEARER,
            "client_assertion": t1,
            "grant_type": "client_credentials",
            "scope": EXCHANGE_SCOPE,
        },
    )
    return _post(
        state["tenantId"],
        {
            "client_id": identity,
            "client_assertion_type": JWT_BEARER,
            "client_assertion": t1,
            "grant_type": "user_fic",
            "user_id": state["agentUserId"],
            "user_federated_identity_credential": t2,
            "scope": scope,
        },
    )


def claims(access_token: str) -> dict:
    return json.loads(base64.urlsafe_b64decode(access_token.split(".")[1] + "==="))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Mint an agentic-user token and print selected claims, not the token.")
    parser.add_argument("scope", nargs="?", default=WORKIQ_SCOPE)
    parser.add_argument("--state", type=Path, default=STATE)
    parser.add_argument("--secret", type=Path, default=SECRET)
    args = parser.parse_args(argv)
    token = agent_user_token(args.scope, state_path=args.state, secret_path=args.secret)
    payload = claims(token)
    print(json.dumps({k: payload.get(k) for k in ("aud", "oid", "upn", "scp", "azp", "xms_par_app_azp", "idtyp")}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
