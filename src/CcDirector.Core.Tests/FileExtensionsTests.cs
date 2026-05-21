using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

public class FileExtensionsTests
{
    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("CHANGELOG.MD", true)]
    [InlineData("notes.markdown", true)]
    [InlineData("page.mdx", true)]
    [InlineData("file.txt", false)]
    [InlineData("image.png", false)]
    [InlineData("", false)]
    public void IsMarkdown_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsMarkdown(path));
    }

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("image.jpg", true)]
    [InlineData("image.jpeg", true)]
    [InlineData("anim.gif", true)]
    [InlineData("icon.ico", true)]
    [InlineData("pic.bmp", true)]
    [InlineData("hero.webp", true)]
    [InlineData("scan.tiff", true)]
    [InlineData("scan.tif", true)]
    [InlineData("file.txt", false)]
    [InlineData("readme.md", false)]
    [InlineData("", false)]
    public void IsImage_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsImage(path));
    }

    [Theory]
    [InlineData("Program.cs", true)]
    [InlineData("PROGRAM.CS", true)]
    [InlineData("script.py", true)]
    [InlineData("index.js", true)]
    [InlineData("app.ts", true)]
    [InlineData("component.tsx", true)]
    [InlineData("component.jsx", true)]
    [InlineData("config.json", true)]
    [InlineData("layout.xml", true)]
    [InlineData("view.xaml", true)]
    [InlineData("cc_blast.csproj", true)]
    [InlineData("lib.fsproj", true)]
    [InlineData("app.vbproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("build.targets", true)]
    [InlineData("BlastApp.sln", true)]
    [InlineData("page.html", false)]
    [InlineData("style.css", true)]
    [InlineData("drawing.svg", true)]
    [InlineData("query.sql", true)]
    [InlineData("run.ps1", true)]
    [InlineData("run.bat", true)]
    [InlineData("run.sh", true)]
    [InlineData("config.yaml", true)]
    [InlineData("config.yml", true)]
    [InlineData("settings.toml", true)]
    [InlineData("main.rs", true)]
    [InlineData("main.go", true)]
    [InlineData("App.java", true)]
    [InlineData("main.cpp", true)]
    [InlineData("main.c", true)]
    [InlineData("header.h", true)]
    [InlineData("header.hpp", true)]
    [InlineData("app.rb", true)]
    [InlineData("index.php", true)]
    [InlineData("app.swift", true)]
    [InlineData("app.kt", true)]
    [InlineData("module.fs", true)]
    [InlineData("script.fsx", true)]
    [InlineData("game.lua", true)]
    [InlineData("analysis.r", true)]
    [InlineData("app.dart", true)]
    [InlineData("app.scala", true)]
    [InlineData("build.groovy", true)]
    [InlineData("build.gradle", true)]
    [InlineData("script.pl", true)]
    [InlineData("module.pm", true)]
    [InlineData("app.ex", true)]
    [InlineData("test.exs", true)]
    [InlineData("core.clj", true)]
    [InlineData("main.zig", true)]
    [InlineData("infra.tf", true)]
    [InlineData("config.hcl", true)]
    [InlineData("schema.graphql", true)]
    [InlineData("schema.gql", true)]
    [InlineData("message.proto", true)]
    [InlineData("file.txt", false)]
    [InlineData("app.log", false)]
    [InlineData(".gitignore", false)]
    [InlineData("Dockerfile", false)]
    [InlineData("readme.md", false)]
    [InlineData("image.png", false)]
    [InlineData("app.exe", false)]
    [InlineData("", false)]
    public void IsCodeFile_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsCodeFile(path));
    }

    [Theory]
    [InlineData("file.txt", true)]
    [InlineData("app.log", true)]
    [InlineData("data.csv", true)]
    [InlineData("settings.ini", true)]
    [InlineData("settings.cfg", true)]
    [InlineData("app.conf", true)]
    [InlineData(".gitignore", true)]
    [InlineData(".editorconfig", true)]
    [InlineData(".dockerignore", true)]
    [InlineData(".env", true)]
    [InlineData(".gitattributes", true)]
    [InlineData(".prettierrc", true)]
    [InlineData(".eslintrc", true)]
    [InlineData("package-lock.lock", true)]
    [InlineData("app.pid", true)]
    [InlineData("changes.diff", true)]
    [InlineData("fix.patch", true)]
    [InlineData("Dockerfile", true)]
    [InlineData("DOCKERFILE", true)]
    [InlineData("Program.cs", false)]
    [InlineData("config.json", false)]
    [InlineData("layout.xml", false)]
    [InlineData("readme.md", false)]
    [InlineData("image.png", false)]
    [InlineData("app.exe", false)]
    [InlineData("lib.dll", false)]
    [InlineData("report.pdf", false)]
    [InlineData("", false)]
    public void IsTextFile_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsTextFile(path));
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("MANUAL.PDF", true)]
    [InlineData("file.txt", false)]
    [InlineData("readme.md", false)]
    [InlineData("image.png", false)]
    [InlineData("", false)]
    public void IsPdf_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsPdf(path));
    }

    [Theory]
    [InlineData("page.html", true)]
    [InlineData("INDEX.HTM", true)]
    [InlineData("notes.md", false)]
    [InlineData("Program.cs", false)]
    [InlineData("report.pdf", false)]
    [InlineData("", false)]
    public void IsHtml_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsHtml(path));
    }

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("photo.png", true)]
    [InlineData("file.txt", true)]
    [InlineData("Program.cs", true)]
    [InlineData("report.pdf", true)]
    [InlineData("Dockerfile", true)]
    [InlineData("BlastApp.sln", true)]
    [InlineData("page.mdx", true)]
    [InlineData("scan.tiff", true)]
    [InlineData("game.lua", true)]
    [InlineData("infra.tf", true)]
    [InlineData("changes.diff", true)]
    [InlineData("app.exe", false)]
    [InlineData("lib.dll", false)]
    [InlineData("archive.zip", false)]
    [InlineData("", false)]
    public void IsViewable_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileExtensions.IsViewable(path));
    }

    [Theory]
    [InlineData("readme.md", FileViewerCategory.Markdown)]
    [InlineData("notes.markdown", FileViewerCategory.Markdown)]
    [InlineData("page.mdx", FileViewerCategory.Markdown)]
    [InlineData("photo.png", FileViewerCategory.Image)]
    [InlineData("scan.tiff", FileViewerCategory.Image)]
    [InlineData("icon.ico", FileViewerCategory.Image)]
    [InlineData("Program.cs", FileViewerCategory.Code)]
    [InlineData("config.json", FileViewerCategory.Code)]
    [InlineData("BlastApp.sln", FileViewerCategory.Code)]
    [InlineData("app.csproj", FileViewerCategory.Code)]
    [InlineData("script.py", FileViewerCategory.Code)]
    [InlineData("page.html", FileViewerCategory.Html)]
    [InlineData("index.htm", FileViewerCategory.Html)]
    [InlineData("game.lua", FileViewerCategory.Code)]
    [InlineData("infra.tf", FileViewerCategory.Code)]
    [InlineData("schema.graphql", FileViewerCategory.Code)]
    [InlineData("file.txt", FileViewerCategory.Text)]
    [InlineData("changes.diff", FileViewerCategory.Text)]
    [InlineData("app.log", FileViewerCategory.Text)]
    [InlineData("Dockerfile", FileViewerCategory.Text)]
    [InlineData(".gitignore", FileViewerCategory.Text)]
    [InlineData("report.pdf", FileViewerCategory.Pdf)]
    [InlineData("app.exe", FileViewerCategory.None)]
    [InlineData("", FileViewerCategory.None)]
    public void GetViewerCategory_ReturnsExpected(string path, FileViewerCategory expected)
    {
        Assert.Equal(expected, FileExtensions.GetViewerCategory(path));
    }
}
