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
        var commandLine = CommandLine.Parse(["--whatever", " ", "app.log", "--data-dir"], WorkingDirectory);

        Assert.Equal([@"C:\work\app.log"], commandLine.Files);
        Assert.Null(commandLine.DataDir);
    }
}
