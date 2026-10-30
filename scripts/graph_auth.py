"""Delegated Microsoft Graph auth helpers for the Iris tenant setup scripts."""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CACHE = HERE / ".msal-cache.json"

# Microsoft Graph Command Line Tools public client.
GRAPH_CLI_CLIENT_ID = "14d82eec-204b-4c2f-b7e8-296a70dab67e"

DEFAULT_TENANT = os.environ.get("AZURE_TENANT_ID") or os.environ.get("GRAPH_TENANT_ID") or "organizations"
DEFAULT_ACCOUNT = os.environ.get("GRAPH_LOGIN_ACCOUNT")
GRAPH = "https://graph.microsoft.com/"

# The scopes the setup scripts use. A script of your own that needs another scope passes it to graph() or token(), and
# you add it at sign-in with --scope.
SCOPES = [
    "https://graph.microsoft.com/AgentIdentityBlueprint.Create",
    "https://graph.microsoft.com/AgentIdentityBlueprint.AddRemoveCreds.All",
    "https://graph.microsoft.com/AgentIdentityBlueprint.UpdateAuthProperties.All",
    "https://graph.microsoft.com/AgentIdentityBlueprintPrincipal.Create",
    "https://graph.microsoft.com/AgentIdUser.ReadWrite.All",
    "https://graph.microsoft.com/AgentCommunicationConfiguration.ReadWrite",
    "https://graph.microsoft.com/DelegatedPermissionGrant.ReadWrite.All",
    "https://graph.microsoft.com/Application.Read.All",
    "https://graph.microsoft.com/User.ReadWrite.All",
    "https://graph.microsoft.com/LicenseAssignment.ReadWrite.All",
]


def full_scope(scope: str) -> str:
    return scope if "://" in scope else GRAPH + scope


def short_scope(scope: str) -> str:
    return scope[len(GRAPH):] if scope.startswith(GRAPH) else scope


def _cache_path(cache_path: str | os.PathLike[str] | None = None) -> Path:
    return Path(cache_path).expanduser().resolve() if cache_path else CACHE


def _app(tenant_id: str | None = None, cache_path: str | os.PathLike[str] | None = None):
    import msal

    cache = msal.SerializableTokenCache()
    path = _cache_path(cache_path)
    if path.exists():
        cache.deserialize(path.read_text(encoding="utf-8"))
    app = msal.PublicClientApplication(
        GRAPH_CLI_CLIENT_ID,
        authority=f"https://login.microsoftonline.com/{tenant_id or DEFAULT_TENANT}",
        token_cache=cache,
    )
    return app, cache, path


def _save(cache, path: Path) -> None:
    if cache.has_state_changed:
        path.write_text(cache.serialize(), encoding="utf-8")


def _account(app, username: str | None):
    accounts = app.get_accounts(username=username) if username else app.get_accounts()
    if not accounts:
        hint = f" for {username}" if username else ""
        raise SystemExit(f"No cached Graph sign-in{hint}. Run: python graph_auth.py login --tenant-id <tenant-id>")
    if len(accounts) > 1 and not username:
        names = ", ".join(a.get("username", "<unknown>") for a in accounts)
        raise SystemExit(f"Multiple cached accounts found ({names}). Set GRAPH_LOGIN_ACCOUNT or pass --account.")
    return accounts[0]


def token(
    scopes: list[str] | tuple[str, ...] | None = None,
    *,
    tenant_id: str | None = None,
    account: str | None = None,
    cache_path: str | os.PathLike[str] | None = None,
) -> str:
    requested = list(scopes or SCOPES)
    app, cache, path = _app(tenant_id, cache_path)
    result = app.acquire_token_silent_with_error(requested, account=_account(app, account or DEFAULT_ACCOUNT))
    _save(cache, path)
    if not result or "access_token" not in result:
        result = result or {}
        extra = "".join(f" --scope {short_scope(s)}" for s in requested if s not in SCOPES)
        raise SystemExit(
            "Could not get a Graph token from the cached sign-in.\n"
            f"error: {result.get('error') or 'no token in the cache for these scopes'}\n"
            f"error_description: {result.get('error_description') or '(none)'}\n"
            f"Sign in again: python graph_auth.py login --tenant-id <tenant-id>{extra}"
        )
    return result["access_token"]


def graph(
    method: str,
    path: str,
    body=None,
    *,
    version: str = "v1.0",
    headers: dict[str, str] | None = None,
    scopes: list[str] | tuple[str, ...] | None = None,
    raw: bytes | None = None,
    content_type: str | None = None,
    tenant_id: str | None = None,
    account: str | None = None,
):
    import requests

    request_headers = {
        "Authorization": "Bearer " + token(scopes, tenant_id=tenant_id, account=account),
        "OData-Version": "4.0",
    }
    if headers:
        request_headers.update(headers)
    url = path if path.startswith("https://") else f"https://graph.microsoft.com/{version}{path}"
    if raw is not None:
        request_headers["Content-Type"] = content_type or "application/octet-stream"
        response = requests.request(method, url, headers=request_headers, data=raw, timeout=60)
    else:
        response = requests.request(method, url, headers=request_headers, json=body, timeout=60)
    if response.status_code >= 400:
        raise RuntimeError(f"{method} {path} -> {response.status_code}: {response.text[:800]}")
    return response.json() if response.content and "json" in response.headers.get("Content-Type", "") else None


def login(args: argparse.Namespace) -> None:
    app, cache, path = _app(args.tenant_id, args.cache)
    scopes = SCOPES + [full_scope(s) for s in args.scope or [] if full_scope(s) not in SCOPES]
    flow = app.initiate_device_flow(scopes=scopes)
    if "user_code" not in flow:
        raise SystemExit(json.dumps(flow, indent=2))
    print("Open", flow.get("verification_uri", "https://microsoft.com/devicelogin"))
    print("Enter code", flow["user_code"])
    result = app.acquire_token_by_device_flow(flow)
    _save(cache, path)
    if "access_token" not in result:
        raise SystemExit(json.dumps({k: result.get(k) for k in ("error", "error_description")}, indent=2))
    claims = result.get("id_token_claims", {})
    print("signed in as", claims.get("preferred_username") or claims.get("upn") or claims.get("oid"))
    print("cache", path)


def whoami(args: argparse.Namespace) -> None:
    access_token = token(tenant_id=args.tenant_id, account=args.account, cache_path=args.cache)
    payload = json.loads(base64.urlsafe_b64decode(access_token.split(".")[1] + "==="))
    print(json.dumps({k: payload.get(k) for k in ("tid", "oid", "upn", "preferred_username", "scp")}, indent=2))


def _add_common(parser: argparse.ArgumentParser, with_defaults: bool) -> None:
    # On the subcommands too, so "login --tenant-id <id>" works as well as "--tenant-id <id> login".
    def default(value):
        return value if with_defaults else argparse.SUPPRESS

    parser.add_argument("--tenant-id", default=default(DEFAULT_TENANT), help="Tenant ID or login tenant. Defaults to AZURE_TENANT_ID, GRAPH_TENANT_ID, or organizations.")
    parser.add_argument("--account", default=default(DEFAULT_ACCOUNT), help="Cached account UPN. Defaults to GRAPH_LOGIN_ACCOUNT.")
    parser.add_argument("--cache", default=default(str(CACHE)), help="MSAL cache path.")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Sign in once and share a delegated Graph token cache with setup scripts.")
    _add_common(parser, with_defaults=True)
    sub = parser.add_subparsers(dest="command")
    login_parser = sub.add_parser("login", help="Run device-code login and write the token cache.")
    _add_common(login_parser, with_defaults=False)
    login_parser.add_argument(
        "--scope",
        action="append",
        help="Another Graph scope to ask for, on top of the default set, for example one your own script needs. "
             "Repeat for more.",
    )
    whoami_parser = sub.add_parser("whoami", help="Print claims from the cached token.")
    _add_common(whoami_parser, with_defaults=False)
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.command == "login":
        login(args)
        return 0
    if args.command == "whoami":
        whoami(args)
        return 0
    parser.print_help()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
