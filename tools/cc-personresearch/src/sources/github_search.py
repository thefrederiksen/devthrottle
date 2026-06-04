"""GitHub API source - search for users by name and email with context filtering."""

import httpx

from cc_personresearch.sources.base import BaseSource
from cc_personresearch.models import SourceResult


class GitHubSource(BaseSource):
    name = "github"
    requires_browser = False

    def fetch(self) -> SourceResult:
        results = {"users": [], "commits_by_email": [], "urls": []}
        found = False
        company = self._context_company()

        # Search commits by email FIRST (most specific, highest signal)
        if self.email:
            commit_resp = httpx.get(
                "https://api.github.com/search/commits",
                params={"q": f"committer-email:{self.email}", "per_page": 5},
                headers={
                    "Accept": "application/vnd.github.cloak-preview+json",
                },
                timeout=15,
            )
            if commit_resp.status_code == 200:
                commit_data = commit_resp.json()
                seen_repos = set()
                for item in commit_data.get("items", [])[:10]:
                    repo = item.get("repository", {})
                    repo_name = repo.get("full_name", "")
                    if repo_name and repo_name not in seen_repos:
                        seen_repos.add(repo_name)
                        results["commits_by_email"].append({
                            "repo": repo_name,
                            "repo_url": repo.get("html_url", ""),
                        })
                        if repo.get("html_url"):
                            results["urls"].append(repo["html_url"])
                        found = True

        # Search users by name
        query = f'"{self.person_name}" in:name'
        resp = httpx.get(
            "https://api.github.com/search/users",
            params={"q": query, "per_page": 5},
            headers={"Accept": "application/vnd.github.v3+json"},
            timeout=15,
        )
        if resp.status_code == 200:
            data = resp.json()
            for user in data.get("items", [])[:5]:
                user_info = {
                    "username": user.get("login", ""),
                    "profile_url": user.get("html_url", ""),
                    "avatar_url": user.get("avatar_url", ""),
                    "type": user.get("type", ""),
                }
                # Fetch user details for bio/company
                detail_resp = httpx.get(
                    user.get("url", ""),
                    headers={"Accept": "application/vnd.github.v3+json"},
                    timeout=10,
                )
                if detail_resp.status_code == 200:
                    details = detail_resp.json()
                    user_info["name"] = details.get("name", "")
                    user_info["company"] = details.get("company", "")
                    user_info["location"] = details.get("location", "")
                    user_info["bio"] = details.get("bio", "")
                    user_info["public_repos"] = details.get("public_repos", 0)
                    user_info["email"] = details.get("email", "")
                    user_info["blog"] = details.get("blog", "")

                    # Flag context match based on company/bio
                    if company:
                        user_company = (details.get("company") or "").lower()
                        user_bio = (details.get("bio") or "").lower()
                        user_info["context_match"] = (
                            company in user_company or company in user_bio
                        )
                    else:
                        user_info["context_match"] = True

                results["users"].append(user_info)
                if user.get("html_url"):
                    results["urls"].append(user["html_url"])
                found = True

        # Sort users: context matches first
        if company and results["users"]:
            results["users"].sort(
                key=lambda u: (not u.get("context_match", False)),
            )

        status = "found" if found else "not_found"
        return SourceResult(source=self.name, status=status, data=results)
