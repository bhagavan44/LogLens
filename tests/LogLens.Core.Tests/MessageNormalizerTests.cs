using LogLens.Core.Analysis;

namespace LogLens.Core.Tests;

public class MessageNormalizerTests
{
    [Theory]
    [InlineData("Order 42 shipped to customer 1234", "Order {n} shipped to customer {n}")]
    [InlineData("User 'alice' signed in from 10.0.0.12", "User {text} signed in from {n}")]
    [InlineData("Said \"hello there\" twice", "Said {text} twice")]
    [InlineData("Request 3f2504e0-4f89-11d3-9a0c-0305e82c3301 failed", "Request {guid} failed")]
    [InlineData("Waited 250ms then 10MB", "Waited {n}ms then {n}MB")]
    [InlineData("Retry 2/3 after 1.5s", "Retry {n}/{n} after {n}s")]
    [InlineData("At 08:00:05.123 on 2026-11-13", "At {n} on {n}")]
    [InlineData("Handle 0x1F4A and id 3f2a9b7c0d1e", "Handle {hex} and id {hex}")]
    [InlineData("Cache hit for key product:77", "Cache hit for key product:{n}")]
    [InlineData("can't connect: it's down", "can't connect: it's down")]
    [InlineData("Error 404: not found", "Error {n}: not found")]
    [InlineData("Connection reset", "Connection reset")]
    [InlineData("", "")]
    public void Replaces_variable_parts_with_placeholders(string message, string expected)
        => Assert.Equal(expected, MessageNormalizer.Normalize(message));

    [Fact]
    public void Only_the_first_line_is_used()
        => Assert.Equal("Failed {n}", MessageNormalizer.Normalize("Failed 7\r\n   more detail 8"));

    [Fact]
    public void Plain_words_that_look_a_bit_like_ids_are_kept()
    {
        Assert.Equal("decade defaced order123 user_42", MessageNormalizer.Normalize("decade defaced order123 user_42"));
    }

    [Fact]
    public void An_unclosed_quote_is_left_alone()
        => Assert.Equal("He said 'unfinished", MessageNormalizer.Normalize("He said 'unfinished"));
}

