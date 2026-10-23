using System.Text;
using VmpLauncher.Core.ModInfo;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class MarkdownLiteTests
{
    private static MdImage Img(string url, string? alt = null) => new(new Uri(url), alt);

    [Fact]
    public void Null_and_blank_input_give_no_blocks()
    {
        Assert.Empty(MarkdownLite.Parse(null));
        Assert.Empty(MarkdownLite.Parse(""));
        Assert.Empty(MarkdownLite.Parse("  \n\r\n \t "));
        Assert.Empty(MarkdownLite.Images(null));
        Assert.Equal("", MarkdownLite.ToPlainText(null));
    }

    [Fact]
    public void Headings_use_the_number_of_hashes_up_to_six()
    {
        var blocks = MarkdownLite.Parse("# One\n## Two ##\n###Three\n####### Seven\n\n#### \n###### Six");

        Assert.Equal(
            new MdBlock[] { new MdHeading(1, "One"), new MdHeading(2, "Two"), new MdHeading(3, "Three"), new MdHeading(6, "Seven"), new MdHeading(6, "Six") },
            blocks);
    }

    [Fact]
    public void A_hash_inside_a_word_is_not_a_closing_marker()
    {
        Assert.Equal(new MdBlock[] { new MdHeading(2, "Written in C#") }, MarkdownLite.Parse("## Written in C#"));
    }

    [Fact]
    public void Equals_underline_makes_a_heading_of_the_line_above()
    {
        Assert.Equal(new MdBlock[] { new MdHeading(1, "Title"), new MdParagraph("Body") }, MarkdownLite.Parse("Title\n=====\n\nBody"));
    }

    [Fact]
    public void List_items_start_with_dash_star_plus_or_a_number()
    {
        var blocks = MarkdownLite.Parse("- dash\n* star\n+ plus\n1. one\n2) two\n   - nested\n\n-5 degrees\n**Note:** not a list");

        Assert.Equal(
            new MdBlock[]
            {
                new MdListItem("dash"), new MdListItem("star"), new MdListItem("plus"), new MdListItem("one"), new MdListItem("two"), new MdListItem("nested"),
                new MdParagraph("-5 degrees Note: not a list"),
            },
            blocks);
    }

    [Fact]
    public void A_wrapped_list_item_continues_on_the_next_line()
    {
        Assert.Equal(new MdBlock[] { new MdListItem("first line second line"), new MdListItem("next") }, MarkdownLite.Parse("- first line\n  second line\n- next"));
    }

    [Fact]
    public void Lines_join_with_a_space_and_blank_lines_split_paragraphs()
    {
        var blocks = MarkdownLite.Parse("one\ntwo\n\nthree\n\n\n\nfour");

        Assert.Equal(new MdBlock[] { new MdParagraph("one two"), new MdParagraph("three"), new MdParagraph("four") }, blocks);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    [InlineData("----------")]
    public void A_line_of_dashes_stars_or_underscores_alone_is_a_rule(string rule)
    {
        Assert.Equal(new MdBlock[] { new MdParagraph("above"), new MdRule(), new MdParagraph("below") }, MarkdownLite.Parse($"above\n{rule}\nbelow"));
    }

    [Fact]
    public void Both_image_syntaxes_become_blocks_in_order_even_mid_paragraph()
    {
        const string md = "Intro ![First](https://i.example.test/a.png \"title\") middle <img src=\"https://i.example.test/b.jpg\" alt='Second' width=\"300\"> end\n\n![](https://i.example.test/c.gif)\n<IMG SRC=https://i.example.test/d.png>";

        Assert.Equal(
            new MdBlock[]
            {
                new MdParagraph("Intro"),
                Img("https://i.example.test/a.png", "First"),
                new MdParagraph("middle"),
                Img("https://i.example.test/b.jpg", "Second"),
                new MdParagraph("end"),
                Img("https://i.example.test/c.gif"),
                Img("https://i.example.test/d.png"),
            },
            MarkdownLite.Parse(md));
    }

    [Fact]
    public void An_image_inside_a_link_is_kept_and_the_link_dropped()
    {
        Assert.Equal(
            new MdBlock[] { Img("https://img.shields.io/badge/build-passing-green.svg", "build"), new MdParagraph("See the site") },
            MarkdownLite.Parse("[![build](https://img.shields.io/badge/build-passing-green.svg)](https://ci.example.test/run/1)\n\nSee [the site](https://example.test)"));
    }

    [Fact]
    public void Only_https_images_survive_and_protocol_relative_ones_are_upgraded()
    {
        const string md = "![http](http://i.example.test/a.png)\n\n![rel](/content/b.png)\n\n![data](data:image/png;base64,AAAA)\n\n"
            + "<img src=\"http://i.example.test/c.png\">\n\n![ok](//cdn.example.test/d.png)\n\n![ok2](https://i.example.test/e.png)\n\n<img alt=\"no source\">";

        Assert.Equal(new MdBlock[] { Img("https://cdn.example.test/d.png", "ok"), Img("https://i.example.test/e.png", "ok2") }, MarkdownLite.Parse(md));
    }

    [Fact]
    public void Entities_in_urls_and_text_are_decoded()
    {
        var blocks = MarkdownLite.Parse("<img src=\"https://i.example.test/a.png?x=1&amp;y=2\" alt=\"A &amp; B\">");

        Assert.Equal(new MdBlock[] { Img("https://i.example.test/a.png?x=1&y=2", "A & B") }, blocks);
    }

    [Fact]
    public void Links_flatten_to_their_text_and_bare_urls_stay()
    {
        var blocks = MarkdownLite.Parse(
            "Read [the docs](https://docs.example.test/a_(b) \"Docs\") or [the forum][forum], see https://example.test/page and <https://auto.example.test/x> too, [empty](https://x.test) [](https://y.test)\n\n[forum]: https://forum.example.test/thread/1");

        Assert.Equal(
            new MdBlock[] { new MdParagraph("Read the docs or the forum, see https://example.test/page and https://auto.example.test/x too, empty") },
            blocks);
    }

    [Fact]
    public void Inline_formatting_markers_are_removed_but_lookalikes_survive()
    {
        var blocks = MarkdownLite.Parse(
            "**bold** and *italic* and __strong__ and _em_ and ***both*** and ~~gone~~ and `code` and snake_case_name and 2 * 3 * 4 and *.cfg files and `*.cfg` and \\*literal\\*");

        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(blocks));
        Assert.Equal("bold and italic and strong and em and both and gone and code and snake_case_name and 2 * 3 * 4 and *.cfg files and *.cfg and *literal*", paragraph.Text);
    }

    [Fact]
    public void Html_tags_are_stripped_and_entities_decoded()
    {
        var blocks = MarkdownLite.Parse("<p align=\"center\"><b>Fast</b> &amp; <i>small</i>&nbsp;&mdash; 5&lt;6 &#169; &copy;</p><!-- hidden -->");

        Assert.Equal(new MdBlock[] { new MdParagraph("Fast & small — 5<6 © ©") }, blocks);
    }

    [Fact]
    public void Html_block_tags_still_separate_paragraphs_headings_and_lists()
    {
        var blocks = MarkdownLite.Parse("<h2>Features</h2><p>First para</p><p>Second<br>line</p><ul><li>one</li><li>two</li></ul><hr>done");

        Assert.Equal(
            new MdBlock[] { new MdHeading(2, "Features"), new MdParagraph("First para"), new MdParagraph("Second line"), new MdListItem("one"), new MdListItem("two"), new MdRule(), new MdParagraph("done") },
            blocks);
    }

    [Fact]
    public void Fenced_code_becomes_one_paragraph_and_is_not_interpreted()
    {
        var blocks = MarkdownLite.Parse("Before\n```cfg\n# not a heading\n- not a list\n![x](https://i.example.test/no.png)\n\n  indented <b>raw</b>\n```\nAfter\n~~~\nsecond\n~~~");

        Assert.Equal(
            new MdBlock[]
            {
                new MdParagraph("Before"),
                new MdParagraph("# not a heading\n- not a list\n![x](https://i.example.test/no.png)\n\n  indented <b>raw</b>"),
                new MdParagraph("After"),
                new MdParagraph("second"),
            },
            blocks);
        Assert.Empty(MarkdownLite.Images("```\n![x](https://i.example.test/no.png)\n```"));
    }

    [Fact]
    public void An_unclosed_fence_runs_to_the_end_and_inline_triple_backticks_are_not_fences()
    {
        Assert.Equal(new MdBlock[] { new MdParagraph("a\nb") }, MarkdownLite.Parse("```\na\nb"));
        Assert.Equal(new MdBlock[] { new MdParagraph("inline then more") }, MarkdownLite.Parse("```inline``` then more"));
        Assert.Equal(new MdBlock[] { new MdParagraph("x"), new MdHeading(1, "Still parsed") }, MarkdownLite.Parse("```x``` \n# Still parsed"));
    }

    [Fact]
    public void Blockquote_markers_and_reference_definitions_are_removed()
    {
        Assert.Equal(new MdBlock[] { new MdParagraph("quoted text here"), new MdParagraph("after") }, MarkdownLite.Parse("> quoted text\n> here\n\n[1]: https://example.test/ref \"Title\"\nafter"));
    }

    [Fact]
    public void Windows_and_old_mac_line_endings_work()
    {
        var blocks = MarkdownLite.Parse("# Title\r\n\r\nline one\r\nline two\r\n\r\n- a\r\n- b\r\rlast\r");

        Assert.Equal(
            new MdBlock[] { new MdHeading(1, "Title"), new MdParagraph("line one line two"), new MdListItem("a"), new MdListItem("b"), new MdParagraph("last") },
            blocks);
    }

    [Fact]
    public void Images_lists_every_https_url_once_in_order()
    {
        const string md = "![a](https://i.example.test/1.png)\n![b](http://i.example.test/skipped.png)\n<img src=\"https://i.example.test/2.png\">\n![again](https://i.example.test/1.png)\n![c](//i.example.test/3.png)";

        Assert.Equal(
            new[] { new Uri("https://i.example.test/1.png"), new Uri("https://i.example.test/2.png"), new Uri("https://i.example.test/3.png") },
            MarkdownLite.Images(md));
    }

    [Fact]
    public void Parse_stops_at_200_blocks_but_Images_still_sees_every_image()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 300; i++)
        {
            sb.Append("![](https://i.example.test/").Append(i).Append(".png)\n\n");
        }

        Assert.Equal(200, MarkdownLite.Parse(sb.ToString()).Count);
        Assert.Equal(300, MarkdownLite.Images(sb.ToString()).Count);
    }

    [Fact]
    public void Very_long_or_pathological_input_is_bounded()
    {
        var marks = string.Concat(Enumerable.Repeat("*a _b `c [d ![e (f <g ", 40_000));
        var started = DateTime.UtcNow;

        var blocks = MarkdownLite.Parse(marks);
        MarkdownLite.Images(marks);
        MarkdownLite.ToPlainText(marks, 200);

        Assert.True(blocks.Count <= 200);
        Assert.All(blocks.OfType<MdParagraph>(), p => Assert.True(p.Text.Length <= 25_000));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "must not run away");
    }

    [Fact]
    public void Private_use_characters_in_the_input_cannot_forge_placeholders()
    {
        var blocks = MarkdownLite.Parse("a " + "0 b " + "5 c");

        Assert.Equal(new MdBlock[] { new MdParagraph("a 0 b 5 c") }, blocks);
    }

    // --- plain text ---

    [Fact]
    public void Plain_text_joins_headings_paragraphs_and_list_items_on_one_line()
    {
        Assert.Equal(
            "Title Some bold text with a link. one two",
            MarkdownLite.ToPlainText("# Title\n\nSome **bold** text with [a link](https://example.test).\n\n![x](https://i.example.test/a.png)\n\n- one\n- two\n\n---"));
    }

    [Fact]
    public void Plain_text_within_the_limit_is_untouched()
    {
        Assert.Equal("short and sweet", MarkdownLite.ToPlainText("short and sweet", 15));
        Assert.Equal("short and sweet", MarkdownLite.ToPlainText("short and sweet"));
    }

    [Fact]
    public void Plain_text_is_cut_at_a_word_with_an_ellipsis_and_never_exceeds_the_limit()
    {
        Assert.Equal("alpha beta…", MarkdownLite.ToPlainText("alpha beta gamma delta", 15));
        Assert.Equal(new string('a', 19) + "…", MarkdownLite.ToPlainText(new string('a', 100), 20));
        Assert.Equal("…", MarkdownLite.ToPlainText("anything", 1));
        Assert.Equal("", MarkdownLite.ToPlainText("anything", 0));
        Assert.Equal("", MarkdownLite.ToPlainText("anything", -4));
        for (var max = 1; max < 60; max++)
        {
            Assert.True(MarkdownLite.ToPlainText("The quick brown fox jumps over the lazy dog, again and again and again.", max).Length <= max);
        }
    }

    [Fact]
    public void Plain_text_does_not_split_a_surrogate_pair()
    {
        // Four chars would end between the two halves of the first emoji; the cut backs off to "ab".
        Assert.Equal("ab…", MarkdownLite.ToPlainText("ab\U0001F680\U0001F680\U0001F680", 4));
    }
}
