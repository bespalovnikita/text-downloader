"""Calculate stable releases for merge commits which are already in main."""

import json
import os
import re
import subprocess
from pathlib import Path
from xml.etree import ElementTree


def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()


def plan(event_name, event, main):
    if event_name == "push":
        before, after = event["before"], event["after"]
        if before == "0" * 40:
            return []
        candidates = git("rev-list", "--reverse", "--first-parent", "--merges", f"{before}..{after}").splitlines()
    else:
        pull = event["pull_request"]
        if not pull["merged"] or pull["base"]["ref"] != "main":
            return []
        candidate = pull["merge_commit_sha"]
        if not re.fullmatch(r"[a-f0-9]{40}", candidate):
            raise ValueError("GitHub did not report a valid merge commit")
        # Push already covers ordinary merge commits, including batched pushes.
        if len(git("show", "-s", "--format=%P", candidate).split()) > 1:
            return []
        candidates = [candidate]

    history = git("rev-list", "--first-parent", main).splitlines()
    position = {commit: index for index, commit in enumerate(history)}
    releases = []
    for commit in candidates:
        if commit not in position:
            raise ValueError(f"{commit} is not on main's first-parent history")
        definition = ElementTree.fromstring(git("show", f"{commit}:Version.props"))
        base_version = definition.findtext("./PropertyGroup/Version")
        baseline = definition.findtext("./PropertyGroup/ReleaseBaselineCommit")
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", base_version or "") or baseline not in position:
            raise ValueError("Version.props has an invalid version or release baseline")
        distance = position[baseline] - position[commit]
        if distance <= 0:
            raise ValueError("The release must follow its baseline on main")
        major, minor, patch = map(int, base_version.split("."))
        releases.append({"commit": commit, "version": f"{major}.{minor}.{patch + distance}"})
    return releases


if __name__ == "__main__":
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    releases = plan(os.environ["GITHUB_EVENT_NAME"], event, "HEAD")
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        print(f"matrix={json.dumps({'include': releases})}", file=output)
        print(f"has_releases={'true' if releases else 'false'}", file=output)
