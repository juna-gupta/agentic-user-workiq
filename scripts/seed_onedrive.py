"""Upload runbook files into Iris's OneDrive using a temporary delegated Graph grant."""

from __future__ import annotations

import argparse
import json
import time
import urllib.parse
from pathlib import Path

from au_token import agent_user_token
from graph_auth import HERE, graph

STATE_PATH = HERE / "au-state.json"
GRAPH_APP_ID = "00000003-0000-0000-c000-000000000000"


def load_state(path: Path) -> dict:
    if not path.exists():
        raise SystemExit(f"Missing {path}. Run create_au.py first.")
    return json.loads(path.read_text(encoding="utf-8"))


def upload_file(token: str, path: Path, folder: str) -> dict:
    import requests

    name = path.name
    content_type = "text/markdown" if name.endswith(".md") else "application/octet-stream"
    url = f"https://graph.microsoft.com/v1.0/me/drive/root:/{folder}/{urllib.parse.quote(name)}:/content"
    last_error = None
    for attempt in range(6):
        response = requests.put(url, headers={"Authorization": "Bearer " + token, "Content-Type": content_type}, data=path.read_bytes(), timeout=60)
        if response.status_code < 300:
            item = response.json()
            print("uploaded", item["name"], item["size"], item["id"], item["webUrl"])
            return item
        last_error = f"{response.status_code}: {response.text[:300]}"
        print("upload attempt", attempt + 1, last_error)
        time.sleep(10)
    raise RuntimeError(f"Upload failed for {path}: {last_error}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Upload files to Iris's OneDrive, then remove the temporary Graph grant.")
    parser.add_argument("files", nargs="*", type=Path, help="Files to upload. Defaults to the sample runbook.")
    parser.add_argument("--state", type=Path, default=STATE_PATH)
    parser.add_argument("--folder", default="Runbooks")
    args = parser.parse_args(argv)
    current = load_state(args.state)
    files = args.files or [HERE.parent / "agent-files" / "helpdesk-bot-runbook.md"]
    for path in files:
        if not path.exists():
            raise SystemExit(f"File not found: {path}")

    graph_sp = graph("GET", f"/servicePrincipals(appId='{GRAPH_APP_ID}')?$select=id")["id"]
    grant = graph(
        "POST",
        "/oauth2PermissionGrants",
        {
            "clientId": current["agentIdentityId"],
            "resourceId": graph_sp,
            "scope": "Files.ReadWrite",
            "consentType": "Principal",
            "principalId": current["agentUserId"],
        },
    )
    print("temporary grant", grant["id"])
    try:
        graph_token = None
        for attempt in range(12):
            try:
                graph_token = agent_user_token("https://graph.microsoft.com/.default")
                break
            except Exception as error:
                print("token not ready yet:", str(error)[:160])
                time.sleep(10)
        if not graph_token:
            raise SystemExit("No Graph token for the agent user.")
        for path in files:
            upload_file(graph_token, path, args.folder)
    finally:
        graph("DELETE", f"/oauth2PermissionGrants/{grant['id']}")
        print("temporary grant removed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
