namespace LogLens.Infrastructure.Tests;

public class CommandLineTests
{
    private const string WorkingDirectory = @"C:\work";

    [Fact]
    public void No_arguments_means_no_files()
    {
        var commandLine = CommandLine.Parse([], WorkingDirectory);

        Assert.Empty(commandLine.Files);
        Assert.Null(commandLine.DataDir);
    }

    [Fact]
    public void Files_are_made_absolute_against_the_working_directory()
    {
        var commandLine = CommandLine.Parse(["app.log", @"D:\logs\other.txt", @"..\up.txt"], WorkingDirectory);

        Assert.Equal([@"C:\work\app.log", @"D:\logs\other.txt", @"C:\up.txt"], commandLine.Files);
    }

    [Fact]
    public void Data_dir_takes_the_next_argument_which_is_not_a_file()
    {
        var commandLine = CommandLine.Parse(["--data-dir", @"D:\scratch", "app.log"], WorkingDirectory);

        Assert.Equal(@"D:\scratch", commandLine.DataDir);
        Assert.Equal([@"C:\work\app.log"], commandLine.Files);
    }

    [Fact]
    public void Unknown_switches_and_blank_arguments_are_ignored()
    {
        var commandLine = CommandLine.Parse(["--whatever", " ", "app.log"], WorkingDirectory);

        Assert.Equal([@"C:\work\app.log"], commandLine.Files);
        Assert.Null(commandLine.DataDir);
    }

    [Theory]
    [InlineData("--merge", "a.log", "b.log")]
    [InlineData("a.log", "--MERGE", "b.log")]
    [InlineData("a.log", "b.log", "--merge")]
    public void Merge_asks_for_one_merged_view_wherever_it_stands(params string[] args)
    {
        var commandLine = CommandLine.Parse(args, WorkingDirectory);

        Assert.True(commandLine.Merge);
        Assert.Equal([@"C:\work\a.log", @"C:\work\b.log"], commandLine.Files);
        Assert.False(CommandLine.Parse(["a.log", "b.log"], WorkingDirectory).Merge);
    }

    [Fact]
    public void A_handed_over_launch_keeps_merge()
    {
        var request = new Instances.LaunchRequest(WorkingDirectory, ["--merge", "a.log", "b.log"]);

        Assert.True(request.Resolve().Merge);
        Assert.Equal([@"C:\work\a.log", @"C:\work\b.log"], request.ResolvePaths());
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--data-dir", "--other")]
    public void Data_dir_without_a_folder_is_empty_rather_than_absent(params string[] args)
    {
        var commandLine = CommandLine.Parse(args, WorkingDirectory);

        Assert.Equal(string.Empty, commandLine.DataDir);
        Assert.Empty(commandLine.Files);
    }
}
