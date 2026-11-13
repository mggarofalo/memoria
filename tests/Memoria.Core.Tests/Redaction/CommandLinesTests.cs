using Memoria.Core.Redaction;

namespace Memoria.Core.Tests.Redaction;

/// <summary>Which command a password flag belongs to, read from the text before it. Each case puts the flag at its last <c>-p</c>.</summary>
public sealed class CommandLinesTests
{
	private static readonly string[] Mysql = ["mysql", "mysqldump"];

	[Theory]
	[InlineData("mysql -u root -pX")]
	[InlineData("sudo env TZ=UTC mysql -u root -pX")]
	[InlineData("$ mysql -u root -pX")]
	[InlineData("PS> mysql -u root -pX")]
	[InlineData("MYSQL_HOST=db mysql -u root -pX")]
	[InlineData("/usr/bin/mysql -u root -pX")]
	[InlineData("C:\\tools\\mysql.exe -u root -pX")]
	[InlineData("echo start; mysql -u root -pX")]
	[InlineData("cat dump.sql | mysql -u root -pX")]
	[InlineData("make && mysqldump -u root -pX")]
	[InlineData("docker exec -it db mysql -u root -pX")]
	[InlineData("kubectl exec pod -- mysql -u root -pX")]
	[InlineData("mysql -u root \\\n  -pX")]
	[InlineData("mysql -u root \\\r\n  -pX")]
	[InlineData("mysql -u root `\n  -pX")]
	[InlineData("mysql -u root ^\n  -pX")]
	[InlineData("x=`date` mysql -u root -pX")]
	public void A_flag_on_the_command_itself_is_at_the_segment_start(string input)
	{
		Assert.Equal(CommandPlace.SegmentStart, Place(input));
	}

	[Theory]
	[InlineData("then I ran mysql -u root -pX")]
	[InlineData("docker run mysql -u root -pX")]
	[InlineData("ssh box mysql --host https://db.test -pX")]
	public void A_command_quoted_after_other_words_is_mid_sentence(string input)
	{
		Assert.Equal(CommandPlace.MidSentence, Place(input));
	}

	[Theory]
	[InlineData("tar -cf out.tar -pX")]
	[InlineData("after mysql finished I ran tar -pX")]
	[InlineData("journalctl -u mysql -pX")]
	[InlineData("mysql -u root\n-pX")]
	[InlineData("mysql -u root\r\n-pX")]
	[InlineData("docker run mysqlx -pX")]
	public void Another_command_or_a_new_line_is_not_the_command(string input)
	{
		Assert.Equal(CommandPlace.None, Place(input));
	}

	[Fact]
	public void Too_many_words_between_the_command_and_the_flag_are_not_one_command()
	{
		string input = "words mysql " + string.Concat(Enumerable.Repeat("-v ", 30)) + "-pX";

		Assert.Equal(CommandPlace.None, Place(input));
	}

	[Fact]
	public void Is_command_says_yes_for_either_place()
	{
		Assert.True(CommandLines.IsCommand("mysql -pX", 6, Mysql));
		Assert.True(CommandLines.IsCommand("then mysql -pX", 11, Mysql));
		Assert.False(CommandLines.IsCommand("tar -pX", 4, Mysql));
	}

	private static CommandPlace Place(string input)
	{
		return CommandLines.Place(input, input.LastIndexOf("-p", StringComparison.Ordinal), Mysql);
	}
}
