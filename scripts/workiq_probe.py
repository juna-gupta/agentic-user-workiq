"""Small Work IQ MCP client for probing as the agentic user."""

from __future__ import annotations

import argparse
import json
import os
import sys

from au_token import agent_user_token

DEFAULT_ENDPOINT = os.environ.get("WORKIQ_MCP_ENDPOINT", "https://workiq.svc.cloud.microsoft/mcp")


class WorkIq:
    def __init__(self, token: str | None = None, endpoint: str = DEFAULT_ENDPOINT):
        self.token = token or agent_user_token()
        self.endpoint = endpoint
        self.session: str | None = None
        self.n = 0
        self._rpc(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "iris-au-probe", "version": "0.1"},
            },
        )
        self._notify("notifications/initialized")

    def _headers(self) -> dict[str, str]:
        headers = {
            "Authorization": "Bearer " + self.token,
            "Accept": "application/json, text/event-stream",
            "Content-Type": "application/json",
        }
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        return headers

    def _post(self, payload: dict, timeout: int):
        import requests

        return requests.post(self.endpoint, headers=self._headers(), json=payload, timeout=timeout)

    @staticmethod
    def _error(method: str, response) -> RuntimeError:
        # 401 and 403 replies often have an empty body. The reason is in WWW-Authenticate.
        parts = [f"{method}: HTTP {response.status_code} {response.reason or ''}".rstrip()]
        for header in ("WWW-Authenticate", "x-ms-request-id", "request-id"):
            if response.headers.get(header):
                parts.append(f"{header}: {response.headers[header]}")
        parts.append("body: " + (response.text[:2000] or "(empty)"))
        return RuntimeError("\n".join(parts))

    def _notify(self, method: str) -> None:
        response = self._post({"jsonrpc": "2.0", "method": method}, timeout=30)
        if response.status_code >= 400:
            raise self._error(method, response)

    def _rpc(self, method: str, params: dict):
        self.n += 1
        response = self._post({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params}, timeout=180)
        if response.status_code >= 400:
            raise self._error(method, response)
        self.session = response.headers.get("Mcp-Session-Id", self.session)
        if "text/event-stream" in response.headers.get("Content-Type", ""):
            messages = [json.loads(line[5:]) for line in response.text.splitlines() if line.startswith("data:")]
            message = next(item for item in messages if item.get("id") == self.n)
        else:
            message = response.json()
        if "error" in message:
            raise RuntimeError(f"{method}: {message['error']}")
        return message["result"]

    def tools(self):
        return self._rpc("tools/list", {})["tools"]

    def call(self, name: str, arguments: dict):
        return self._rpc("tools/call", {"name": name, "arguments": arguments})

    def fetch(self, *paths: str):
        result = self.call("fetch", {"entityUrls": list(paths)})
        structured = result.get("structuredContent")
        if structured is None:
            for block in reversed(result.get("content", [])):
                if block.get("type") != "text":
                    continue
                try:
                    structured = json.loads(block["text"])
                    break
                except json.JSONDecodeError:
                    continue
        return result.get("isError"), structured


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Probe Work IQ MCP with an agentic-user token.")
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("tools", help="List available MCP tools.")
    fetch_parser = sub.add_parser("fetch", help="Call the Work IQ fetch tool for one or more entity URLs.")
    fetch_parser.add_argument("paths", nargs="+")
    args = parser.parse_args(argv)

    sys.stdout.reconfigure(encoding="utf-8")
    try:
        run(args)
    except (RuntimeError, OSError) as error:
        print("Work IQ probe failed.", file=sys.stderr)
        print(f"{type(error).__name__}: {error}", file=sys.stderr)
        print("If it failed right after create_au.py, run it again after a minute.", file=sys.stderr)
        return 1
    return 0


def run(args: argparse.Namespace) -> None:
    client = WorkIq(endpoint=args.endpoint)
    if args.command == "tools":
        for tool in client.tools():
            print("==", tool["name"], "|", (tool.get("description") or "")[:300].replace("\n", " "))
            print("   schema:", json.dumps(tool.get("inputSchema", {}).get("properties", {}))[:700])
    elif args.command == "fetch":
        is_error, payload = client.fetch(*args.paths)
        print(json.dumps({"isError": is_error, "result": payload}, indent=2))


if __name__ == "__main__":
    raise SystemExit(main())
