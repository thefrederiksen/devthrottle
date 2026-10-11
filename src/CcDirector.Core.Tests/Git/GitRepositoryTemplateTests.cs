using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// The once-per-class repository template keeps tests as isolated as a repository built per test did:
/// a copy pushes to its OWN origin, never to the template's or to another copy's.
/// </summary>
public sealed class GitRepositoryTemplateTests
{
    public sealed class Template : GitRepositoryTemplate
    {
        public Template() : base("ccd-template-test-") { }

        protected override IReadOnlyList<string> Clones => new[] { "repo" };

        protected override void Build(string root)
        {
            var origin = Path.Combine(root, "origin.git");
            var repo = Path.Combine(root, "repo");
            RunGit(root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
            RunGit(root, "-c", "init.defaultBranch=main", "clone", origin, repo);
            RunGit(repo, "config", "user.email", "test@cc-director.local");
            RunGit(repo, "config", "user.name", "CC Director Test");
            RunGit(repo, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(repo, "README.md"), "initial\n");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "initial commit");
            RunGit(repo, "branch", "-M", "main");
            RunGit(repo, "push", "-u", "origin", "main");
        }

        public static string Git(string workingDirectory, params string[] args) => RunGit(workingDirectory, args);
    }

    [Fact]
    public void A_copy_pushes_to_its_own_origin_and_neither_the_template_nor_another_copy_sees_it()
    {
        using var template = new Template();
        var first = template.CopyTo();
        var second = template.CopyTo();
        try
        {
            var firstRepo = Path.Combine(first, "repo");
            Assert.Equal(Path.Combine(first, "origin.git"), Template.Git(firstRepo, "remote", "get-url", "origin").Trim());
            Assert.Equal(Path.Combine(second, "origin.git"), Template.Git(Path.Combine(second, "repo"), "remote", "get-url", "origin").Trim());

            Template.Git(firstRepo, "checkout", "-b", "only-in-the-first-copy");
            Template.Git(firstRepo, "commit", "--allow-empty", "-m", "first copy's work");
            Template.Git(firstRepo, "push", "-u", "origin", "only-in-the-first-copy");

            Assert.Contains("only-in-the-first-copy", Template.Git(Path.Combine(first, "origin.git"), "branch", "--list"));
            Assert.DoesNotContain("only-in-the-first-copy", Template.Git(Path.Combine(second, "origin.git"), "branch", "--list"));

            // A third copy taken after the push is still the pristine template.
            var third = template.CopyTo();
            try
            {
                Assert.DoesNotContain("only-in-the-first-copy", Template.Git(Path.Combine(third, "origin.git"), "branch", "--list"));
                Assert.Equal("main", Template.Git(Path.Combine(third, "repo"), "branch", "--show-current").Trim());
            }
            finally
            {
                TestTempRoot.DeleteTree(third);
            }
        }
        finally
        {
            TestTempRoot.DeleteTree(first);
            TestTempRoot.DeleteTree(second);
        }
    }

    [Fact]
    public void A_worktree_of_a_copy_pushes_to_the_copys_origin()
    {
        // Worktrees share their clone's configuration; this is why one set-url per clone is enough, and why
        // a relative origin would not have been (git resolves it against the worktree, not the clone).
        using var template = new Template();
        var root = template.CopyTo();
        try
        {
            var repo = Path.Combine(root, "repo");
            var worktree = Path.Combine(root, "wt");
            Template.Git(repo, "worktree", "add", "-b", "from-a-worktree", worktree, "main");
            Template.Git(worktree, "commit", "--allow-empty", "-m", "worktree work");
            Template.Git(worktree, "push", "-u", "origin", "from-a-worktree");

            Assert.Contains("from-a-worktree", Template.Git(Path.Combine(root, "origin.git"), "branch", "--list"));
        }
        finally
        {
            TestTempRoot.DeleteTree(root);
        }
    }

    [Fact]
    public void Disposing_the_template_removes_its_folder_and_leaves_the_copies()
    {
        var template = new Template();
        var copy = template.CopyTo();
        try
        {
            var templateOrigin = Template.Git(Path.Combine(copy, "repo"), "remote", "get-url", "origin");
            Assert.True(Directory.Exists(copy));

            template.Dispose();

            Assert.True(Directory.Exists(copy), "disposing the template must not touch a copy a test still owns");
            Assert.True(File.Exists(Path.Combine(copy, "repo", "README.md")));
            Assert.Equal(Path.Combine(copy, "origin.git"), templateOrigin.Trim());
        }
        finally
        {
            TestTempRoot.DeleteTree(copy);
        }
    }
}
