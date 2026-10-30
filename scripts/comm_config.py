"""Get, set, or restore the blueprint communicationConfiguration."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from graph_auth import HERE, graph

STATE = HERE / "au-state.json"
BACKUP = HERE / "comm-config-backup.json"
SCOPES = ["https://graph.microsoft.com/AgentCommunicationConfiguration.ReadWrite"]
KEYS = {
    "group": "groupChatConfiguration",
    "channel": "channelConfiguration",
    "one": "oneOnOneChatConfiguration",
    "meeting": "meetingChatConfiguration",
}
MODES = {"all": "allMessages", "mention": "atMentionedMessagesOnly"}


def load_state(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def paths(args: argparse.Namespace) -> tuple[str, str | None]:
    state = load_state(args.state)
    blueprint = args.blueprint_object_id or state.get("blueprintObjectId")
    identity = args.agent_identity_id or state.get("agentIdentityId")
    if not blueprint:
        raise SystemExit("Missing blueprint object ID. Run create_au.py first or pass --blueprint-object-id.")
    bp_path = f"/applications/{blueprint}/microsoft.graph.agentIdentityBlueprint/communicationConfiguration"
    id_path = (
        f"/servicePrincipals/microsoft.graph.agentIdentity/{identity}/communicationConfiguration"
        if identity
        else None
    )
    return bp_path, id_path


def get(path: str) -> dict:
    return graph("GET", path, version="beta", scopes=SCOPES)


def summary(config: dict) -> dict:
    teamwork = config.get("teamworkConfiguration") or {}
    modes = {key: (teamwork.get(value) or {}).get("messageNotificationMode") for key, value in KEYS.items()}
    endpoint = config.get("endpointConfiguration") or {}
    return {
        "modes": modes,
        "endpoint": endpoint.get("configurationType"),
        "callback": (endpoint.get("apiBased") or {}).get("callbackUri"),
        "overridable": config.get("isOverridableAtAgentIdLevel"),
    }


def put_body(config: dict) -> dict:
    return {
        key: config[key]
        for key in ("isOverridableAtAgentIdLevel", "endpointConfiguration", "teamworkConfiguration")
        if key in config
    }


def apply_assignments(body: dict, assignments: list[str]) -> None:
    teamwork = body.setdefault("teamworkConfiguration", {})
    for assignment in assignments:
        key, sep, value = assignment.partition("=")
        if key == "endpoint" and sep == "=" and value.startswith("https://"):
            body["endpointConfiguration"] = {"configurationType": "apiBased", "apiBased": {"callbackUri": value}}
            continue
        if sep != "=" or key not in KEYS or value not in MODES:
            choices = ", ".join(f"{k}=all|mention" for k in KEYS)
            raise SystemExit(f"Invalid assignment '{assignment}'. Use {choices}, or endpoint=https://<host>/api/messages.")
        teamwork[KEYS[key]] = {"messageNotificationMode": MODES[value]}
    if "endpointConfiguration" not in body:
        raise SystemExit("The blueprint has no endpoint yet, and a PUT without one stops all delivery. "
                         "Add endpoint=https://<host>/api/messages.")


def print_summary(label: str, path: str | None) -> None:
    if not path:
        return
    try:
        config = get(path)
    except RuntimeError as error:
        if "-> 404" not in str(error):
            raise
        print(f"{label}: no configuration yet. Set one with: "
              "python comm_config.py set endpoint=https://<host>/api/messages")
        return
    print(f"{label}:", json.dumps(summary(config)))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Read or replace the Teams delivery configuration. set does a full PUT replacement."
    )
    parser.add_argument("--state", type=Path, default=STATE)
    parser.add_argument("--backup", type=Path, default=BACKUP)
    parser.add_argument("--blueprint-object-id")
    parser.add_argument("--agent-identity-id")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("get", help="Print blueprint and agent identity communicationConfiguration summaries.")
    set_parser = sub.add_parser(
        "set",
        help="Set delivery modes and/or the endpoint. Example: group=all channel=all meeting=all "
             "endpoint=https://<host>/api/messages")
    set_parser.add_argument("assignments", nargs="+")
    sub.add_parser("restore", help="Restore the blueprint config from comm-config-backup.json.")
    args = parser.parse_args(argv)

    bp_path, id_path = paths(args)
    if args.command == "set":
        try:
            current = get(bp_path)
        except RuntimeError as error:
            if "-> 404" not in str(error):
                raise
            current = {}
        if current and not args.backup.exists():
            args.backup.write_text(json.dumps({"blueprint": current}, indent=2) + "\n", encoding="utf-8")
            print("backup written", args.backup)
        body = put_body(current)
        apply_assignments(body, args.assignments)
        graph("PUT", bp_path, body, version="beta", scopes=SCOPES)
    elif args.command == "restore":
        if not args.backup.exists():
            raise SystemExit(f"No backup at {args.backup}. comm_config.py set writes one only when a previous "
                             "configuration existed, so there is nothing to restore.")
        backup = json.loads(args.backup.read_text(encoding="utf-8"))["blueprint"]
        graph("PUT", bp_path, put_body(backup), version="beta", scopes=SCOPES)

    print_summary("blueprint", bp_path)
    print_summary("identity ", id_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
