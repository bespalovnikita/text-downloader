"""Verify release numbering against actual first-parent merge history."""

import os
import runpy
import subprocess
import tempfile
from pathlib import Path

plan = runpy.run_path(str(Path(__file__).with_name("plan-releases.py")))["plan"]


def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()


def version_props(version, baseline, new_series=False):
    mode = "<ReleaseBaselineMode>version-introduction</ReleaseBaselineMode>" if new_series else ""
    return (
        f"<Project><PropertyGroup><Version>{version}</Version>"
        f"<ReleaseBaselineCommit>{baseline}</ReleaseBaselineCommit>{mode}</PropertyGroup></Project>"
    )


def merge(branch, definition=None):
    git("switch", "-c", branch)
    Path(branch).write_text(branch, encoding="utf-8")
    git("add", branch)
    if definition is not None:
        Path("Version.props").write_text(definition, encoding="utf-8")
        git("add", "Version.props")
    git("commit", "-m", f"Prepare {branch}")
    git("switch", "main")
    git("merge", "--no-ff", "-m", f"Merge {branch}", branch)
    return git("rev-parse", "HEAD")


with tempfile.TemporaryDirectory(prefix="release-planner-") as repository:
    original = Path.cwd()
    try:
        os.chdir(repository)
        git("init", "-b", "main")
        git("config", "user.name", "Release test")
        git("config", "user.email", "release-test@example.test")
        git("commit", "--allow-empty", "-m", "Release baseline")
        baseline = git("rev-parse", "HEAD")
        previous = merge("previous", version_props("0.1.7", baseline))
        merge("unrelated")
        major = merge("major", version_props("1.0.0", baseline, new_series=True))
        patch = merge("patch")
        result = plan("push", {"before": baseline, "after": patch}, "main")
        releases = {item["commit"]: item["version"] for item in result}
        assert releases[previous] == "0.1.8", result
        assert releases[major] == "1.0.0", result
        assert releases[patch] == "1.0.1", result
    finally:
        os.chdir(original)
