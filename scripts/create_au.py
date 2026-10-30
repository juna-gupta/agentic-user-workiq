"""Create the Agent 365 / Entra Agent ID chain for the Iris agentic-user sample."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path

from graph_auth import HERE, graph

STATE = HERE / "au-state.json"
SECRET = HERE / ".blueprint-secret.json"

# First-party resource apps.
SMBA_APP_ID = "5a807f24-c9de-44ee-a3a7-329e88a00ffc"
WORKIQ_APP_ID = "fdcc1f02-fc51-4226-8753-f668596af7f7"

# Global product SKU IDs. Override with --license-sku or AGENTIC_USER_LICENSE_SKUS if your tenant uses different plans.
DEFAULT_LICENSE_SKUS = [
    "7e31c0d9-9551-471d-836f-32ee72be4a01",
    "46c3a859-c90d-40b3-9551-6178a48d5c18",
]

DEFAULT_PROFILE = {
    "jobTitle": "Incident Agent",
    "department": "Helpdesk Engineering",
    "officeLocation": "Building 25",
}


def load(path: Path = STATE) -> dict:
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def save(state: dict, path: Path = STATE) -> None:
    path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")


def bind_user(user_id: str) -> str:
    return f"https://graph.microsoft.com/v1.0/users/{user_id}"


def current_user() -> dict:
    return graph("GET", "/me?$select=id,userPrincipalName,displayName")


def tenant_id() -> str:
    orgs = graph("GET", "/organization?$select=id")
    return orgs["value"][0]["id"]


def service_principal_id(app_id: str) -> str:
    existing = graph("GET", f"/servicePrincipals?$filter=appId eq '{app_id}'&$select=id")["value"]
    if existing:
        return existing[0]["id"]
    return graph("POST", "/servicePrincipals", {"appId": app_id})["id"]


def grant(client_id: str, resource_id: str, scope: str, principal_id: str | None = None) -> str:
    query = f"/oauth2PermissionGrants?$filter=clientId eq '{client_id}' and resourceId eq '{resource_id}'"
    for existing in graph("GET", query)["value"]:
        if existing.get("principalId") == principal_id and scope in existing.get("scope", "").split():
            return existing["id"]
    body = {
        "clientId": client_id,
        "resourceId": resource_id,
        "scope": scope,
        "consentType": "Principal" if principal_id else "AllPrincipals",
    }
    if principal_id:
        body["principalId"] = principal_id
    return graph("POST", "/oauth2PermissionGrants", body)["id"]


def blueprint_token(state: dict, secret_path: Path) -> str:
    import requests

    secret = json.loads(secret_path.read_text(encoding="utf-8"))["secretText"]
    url = f"https://login.microsoftonline.com/{state['tenantId']}/oauth2/v2.0/token"
    for attempt in range(12):
        response = requests.post(
            url,
            data={
                "client_id": state["blueprintAppId"],
                "client_secret": secret,
                "grant_type": "client_credentials",
                "scope": "https://graph.microsoft.com/.default",
            },
            timeout=30,
        )
        if response.ok:
            return response.json()["access_token"]
        print("blueprint token attempt", attempt + 1, response.text[:240])
        time.sleep(15)
    raise SystemExit("Blueprint token was not issued before the retry limit.")


def create_agent_identity(state: dict, name: str, sponsor_id: str, secret_path: Path) -> str:
    import requests

    url = "https://graph.microsoft.com/beta/servicePrincipals/Microsoft.Graph.AgentIdentity"
    body = {
        "displayName": name,
        "agentIdentityBlueprintId": state["blueprintAppId"],
        "sponsors@odata.bind": [bind_user(sponsor_id)],
    }
    for attempt in range(6):
        response = requests.post(
            url,
            headers={"Authorization": "Bearer " + blueprint_token(state, secret_path), "OData-Version": "4.0"},
            json=body,
            timeout=60,
        )
        if response.status_code < 300:
            return response.json()["id"]
        print("agent identity attempt", attempt + 1, response.status_code, response.text[:300])
        time.sleep(15)
    raise SystemExit("Could not create the agent identity.")


def license_skus(values: list[str] | None) -> list[str]:
    if values:
        return values
    env = os.environ.get("AGENTIC_USER_LICENSE_SKUS")
    if env:
        return [part.strip() for part in env.replace(";", ",").split(",") if part.strip()]
    return DEFAULT_LICENSE_SKUS


def put_avatar(user_id: str, avatar_path: Path) -> None:
    if not avatar_path.exists():
        print("avatar skipped; file not found:", avatar_path)
        return
    # A new user's mailbox takes a few minutes to be ready, and the photo lives there. Until then Graph returns 404.
    waits = [15, 30, 45, 60, 60, 60]
    for attempt in range(len(waits) + 1):
        try:
            graph(
                "PUT",
                f"/users/{user_id}/photo/$value",
                raw=avatar_path.read_bytes(),
                content_type="image/png",
            )
            print("avatar set")
            return
        except RuntimeError as error:
            match = re.search(r"-> (\d{3}):", str(error))
            status = int(match.group(1)) if match else 0
            if attempt == len(waits) or not (status in (404, 429) or status >= 500):
                print("WARNING: avatar not set yet:", str(error)[:300])
                print("The mailbox may still be getting ready. Run create_au.py again later to set the photo. "
                      "It reuses the objects in au-state.json, so it is safe to re-run.")
                return
            print(f"avatar attempt {attempt + 1} got {status}; mailbox may not be ready yet, retrying in {waits[attempt]}s")
            time.sleep(waits[attempt])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Create or update Iris's blueprint, agent identity, and agentic user.")
    parser.add_argument("--state", type=Path, default=STATE, help="State file written by this script.")
    parser.add_argument("--secret", type=Path, default=SECRET, help="Blueprint client secret file written by this script.")
    parser.add_argument("--blueprint-name", default="Incident Teammate Blueprint")
    parser.add_argument("--identity-name", default="Iris Incident Agent")
    parser.add_argument("--au-name", default="Iris (Incident Agent)")
    parser.add_argument("--au-nick", default="iris.agent")
    parser.add_argument("--au-upn", help="Full UPN for the agentic user. Defaults to <au-nick>@<domain>.")
    parser.add_argument("--domain", help="Verified domain for the agentic user. Defaults to the signed-in admin's domain.")
    parser.add_argument("--manager-id", help="Manager/sponsor user object ID. Defaults to the signed-in admin.")
    parser.add_argument("--manager-upn", help="Manager/sponsor UPN. Overrides --manager-id when supplied.")
    parser.add_argument("--usage-location", default="US")
    parser.add_argument("--job-title", default=DEFAULT_PROFILE["jobTitle"], help="Job title on the agentic user's profile.")
    parser.add_argument("--department", default=DEFAULT_PROFILE["department"], help="Department on the agentic user's profile.")
    parser.add_argument("--office", default=DEFAULT_PROFILE["officeLocation"], help="Office location on the agentic user's profile.")
    parser.add_argument("--license-sku", action="append", help="SKU ID to assign. Repeat for multiple. Defaults can be overridden with AGENTIC_USER_LICENSE_SKUS.")
    parser.add_argument("--avatar", type=Path, default=HERE / "iris-avatar.png")
    parser.add_argument("--skip-avatar", action="store_true")
    args = parser.parse_args(argv)

    state = load(args.state)
    me = current_user()
    state.setdefault("tenantId", tenant_id())

    manager_id = args.manager_id or me["id"]
    if args.manager_upn:
        manager = graph("GET", f"/users/{args.manager_upn}?$select=id")
        manager_id = manager["id"]
    state.setdefault("managerUserId", manager_id)

    if "blueprintAppId" not in state:
        blueprint = graph(
            "POST",
            "/applications",
            {
                "@odata.type": "Microsoft.Graph.AgentIdentityBlueprint",
                "displayName": args.blueprint_name,
                "sponsors@odata.bind": [bind_user(manager_id)],
                "owners@odata.bind": [bind_user(manager_id)],
            },
        )
        state.update(blueprintObjectId=blueprint["id"], blueprintAppId=blueprint["appId"])
        save(state, args.state)
        print("blueprint", blueprint["appId"])

    if not args.secret.exists():
        end = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 180 * 86400))
        password = graph(
            "POST",
            f"/applications/{state['blueprintObjectId']}/addPassword",
            {"passwordCredential": {"displayName": "local setup", "endDateTime": end}},
        )
        args.secret.write_text(
            json.dumps(
                {"secretText": password["secretText"], "keyId": password["keyId"], "endDateTime": password["endDateTime"]},
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        print("blueprint secret added; expires", password["endDateTime"])

    if "blueprintPrincipalId" not in state:
        existing = graph("GET", f"/servicePrincipals?$filter=appId eq '{state['blueprintAppId']}'&$select=id")["value"]
        principal_id = existing[0]["id"] if existing else graph(
            "POST",
            "/servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal",
            {"appId": state["blueprintAppId"]},
        )["id"]
        state["blueprintPrincipalId"] = principal_id
        save(state, args.state)
        print("blueprint principal", principal_id)

    if "agentIdentityId" not in state:
        state["agentIdentityId"] = create_agent_identity(state, args.identity_name, manager_id, args.secret)
        save(state, args.state)
        print("agent identity", state["agentIdentityId"])

    smba_principal = service_principal_id(SMBA_APP_ID)
    grant(state["blueprintPrincipalId"], smba_principal, "AgentData.ReadWrite")
    grant(state["agentIdentityId"], smba_principal, "AgentData.ReadWrite")
    print("Teams Messaging Bot API grants ok")

    domain = args.domain or me["userPrincipalName"].split("@", 1)[1]
    au_upn = args.au_upn or f"{args.au_nick}@{domain}"
    if "agentUserId" not in state:
        found = graph("GET", f"/users?$filter=userPrincipalName eq '{au_upn}'&$select=id")["value"]
        if found:
            state["agentUserId"] = found[0]["id"]
        else:
            user = graph(
                "POST",
                "/users/microsoft.graph.agentUser",
                {
                    "accountEnabled": True,
                    "displayName": args.au_name,
                    "mailNickname": args.au_nick,
                    "userPrincipalName": au_upn,
                    "identityParentId": state["agentIdentityId"],
                    "usageLocation": args.usage_location,
                },
                version="beta",
            )
            state["agentUserId"] = user["id"]
        state["agentUserUpn"] = au_upn
        save(state, args.state)
        print("agentic user", state["agentUserId"], au_upn)

    profile = {"jobTitle": args.job_title, "department": args.department, "officeLocation": args.office}
    graph("PATCH", f"/users/{state['agentUserId']}", profile, version="beta")
    graph("PUT", f"/users/{state['agentUserId']}/manager/$ref", {"@odata.id": bind_user(manager_id)})
    print("profile and manager set")

    skus = license_skus(args.license_sku)
    assigned = graph("GET", f"/users/{state['agentUserId']}?$select=assignedLicenses")["assignedLicenses"]
    have = {license_item["skuId"].lower() for license_item in assigned}
    missing = [sku for sku in skus if sku.lower() not in have]
    if missing:
        graph(
            "POST",
            f"/users/{state['agentUserId']}/assignLicense",
            {"addLicenses": [{"skuId": sku, "disabledPlans": []} for sku in missing], "removeLicenses": []},
        )
    print("licenses ok")

    grant(state["agentIdentityId"], service_principal_id(WORKIQ_APP_ID), "WorkIQAgent.Ask", principal_id=state["agentUserId"])
    print("Work IQ grant ok")

    if not args.skip_avatar:
        put_avatar(state["agentUserId"], args.avatar)

    print(json.dumps(state, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
