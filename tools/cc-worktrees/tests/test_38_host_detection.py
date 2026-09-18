"""Which host a remote URL is on.

The host layer may only ask a host it recognises. A URL it cannot place is the "other" host: git-only
checks and no host proof, never a guess at an organisation or a repository name. These are the same shapes
the product already recognises in src/CcDirector.Core/Utilities/GitHubUrls.cs.
"""

from __future__ import annotations

import host


def test_github_https_ssh_and_scp_forms_are_github():
    for url in ("https://github.com/an-owner/a-repo.git",
                "https://github.com/an-owner/a-repo",
                "git@github.com:an-owner/a-repo.git",
                "ssh://git@github.com/an-owner/a-repo.git"):
        remote = host.parse_remote(url)
        assert remote.host == host.GITHUB, url
        assert remote.owner == "an-owner", url
        assert remote.repo == "a-repo", url
        assert remote.name == "an-owner/a-repo", url


def test_azure_devops_https_and_ssh_forms_are_azure_devops():
    for url in ("https://dev.azure.com/an-org/a-project/_git/a-repo",
                "https://dev.azure.com/an-org/a-project/_git/a-repo.git",
                "https://someone@dev.azure.com/an-org/a-project/_git/a-repo",
                "git@ssh.dev.azure.com:v3/an-org/a-project/a-repo"):
        remote = host.parse_remote(url)
        assert remote.host == host.AZURE_DEVOPS, url
        assert (remote.owner, remote.project, remote.repo) == ("an-org", "a-project", "a-repo"), url
        assert remote.org_url == "https://dev.azure.com/an-org", url


def test_the_legacy_visualstudio_form_is_azure_devops():
    for url, project in (("https://an-org.visualstudio.com/a-project/_git/a-repo", "a-project"),
                         ("https://an-org.visualstudio.com/DefaultCollection/a-project/_git/a-repo.git",
                          "a-project")):
        remote = host.parse_remote(url)
        assert remote.host == host.AZURE_DEVOPS, url
        assert (remote.owner, remote.project, remote.repo) == ("an-org", project, "a-repo"), url
        # The organisation URL an older remote needs is the old one, not dev.azure.com.
        assert remote.org_url == "https://an-org.visualstudio.com", url


def test_a_url_encoded_project_name_is_read_back_as_the_real_name():
    remote = host.parse_remote("https://dev.azure.com/an%20org/a%20project/_git/a%20repo")
    assert (remote.owner, remote.project, remote.repo) == ("an org", "a project", "a repo")


def test_anything_else_is_the_other_host_and_names_nothing():
    for url in ("https://gitlab.com/an-org/a-repo.git",
                "https://git.example.invalid/an-org/a-repo.git",
                "git@bitbucket.org:an-org/a-repo.git",
                r"C:\Users\someone\repos\a-repo.git",
                "/srv/git/a-repo.git",
                ""):
        remote = host.parse_remote(url)
        assert remote.host == host.OTHER, url
        assert remote.owner == "" and remote.project == "" and remote.repo == "", url


def test_a_host_name_inside_a_path_is_not_that_host():
    # The match is anchored at the end of the URL, so a directory called "github.com" on a file remote
    # does not turn a local clone into a GitHub repository.
    assert host.parse_remote("/srv/mirrors/github.com/an-org/a-repo.git/extra").host == host.OTHER


def test_the_other_host_is_never_asked_and_says_so(local_world, monkeypatch):
    """A local bare remote is on no host: the answer is the plain note, and no tool is run for it."""
    monkeypatch.setattr(host, "pull_requests", _never_called)
    proofs, note = host.prove(local_world.repo, "main", "0" * 40, ["1" * 40], timeout=5.0)
    assert proofs == ()
    assert note is not None and note.startswith("checked by git only:")
    assert "no host cc-worktrees can ask" in note


def _never_called(*args, **kwargs):
    raise AssertionError("the host was asked about a repository that is on no host")
