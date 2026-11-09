using SoundLibert.Domain.Video;

namespace SoundLibert.Domain.Tests;

public class BlackBarMeterTests
{
    private const int W = 640;
    private const int H = 360;

    // One more than the frames in a row the meter wants to see holding still.
    private const int EnoughFrames = 7;

    /// <summary>A grey frame with black margins, as a video decoder would deliver it.</summary>
    private static byte[] Frame(int top = 0, int bottom = 0, int left = 0, int right = 0,
        int width = W, int height = H, byte picture = 170, byte black = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inPicture = y >= top && y < height - bottom && x >= left && x < width - right;
                var level = inPicture ? picture : black;
                var at = (y * width + x) * 4;
                pixels[at] = level;
                pixels[at + 1] = level;
                pixels[at + 2] = level;
                pixels[at + 3] = 255;
            }
        }
        return pixels;
    }

    private static BlackBars Feed(BlackBarMeter meter, byte[] frame, int times = EnoughFrames,
        int width = W, int height = H)
    {
        for (var i = 0; i < times; i++) meter.Measure(frame, width, height);
        return meter.Bars;
    }

    [Fact]
    public void Full_picture_is_left_alone()
    {
        Assert.Equal(default, Feed(new BlackBarMeter(), Frame(), times: 30));
    }

    [Fact]
    public void Cinema_format_bars_are_cut_with_one_line_of_margin()
    {
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(new BlackBarMeter(), Frame(top: 40, bottom: 40)));
    }

    [Fact]
    public void Black_sides_of_an_old_clip_are_cut()
    {
        Assert.Equal(new BlackBars(0, 0, 81, 81), Feed(new BlackBarMeter(), Frame(left: 80, right: 80)));
    }

    [Fact]
    public void Bars_on_all_four_sides_are_cut()
    {
        Assert.Equal(new BlackBars(21, 21, 31, 31),
            Feed(new BlackBarMeter(), Frame(top: 20, bottom: 20, left: 30, right: 30)));
    }

    [Fact]
    public void Nothing_is_cut_until_the_bars_have_held_still()
    {
        var meter = new BlackBarMeter();
        var frame = Frame(top: 40, bottom: 40);

        Assert.Equal(default, Feed(meter, frame, times: EnoughFrames - 1));
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(meter, frame, times: 1));
    }

    [Fact]
    public void Picture_fading_in_from_black_is_not_taken_for_bars()
    {
        var meter = new BlackBarMeter();

        // The lit part of the picture grows towards the edges a little every frame.
        for (var margin = 80; margin >= 0; margin -= 4)
        {
            meter.Measure(Frame(top: margin, bottom: margin), W, H);
            Assert.Equal(default, meter.Bars);
        }
    }

    [Fact]
    public void Dark_opening_corrects_itself_when_the_picture_reaches_the_edges()
    {
        var meter = new BlackBarMeter();

        // A singer in a spotlight on a black stage looks exactly like bars...
        Assert.Equal(new BlackBars(61, 61, 0, 0), Feed(meter, Frame(top: 60, bottom: 60)));

        // ...until the lights come on.
        Assert.Equal(default, Feed(meter, Frame(), times: 1));
    }

    [Fact]
    public void Dark_scene_inside_real_bars_only_narrows_the_cut_down_to_the_bars()
    {
        var meter = new BlackBarMeter();
        Assert.Equal(new BlackBars(61, 61, 0, 0), Feed(meter, Frame(top: 60, bottom: 60)));

        // The first bright scene reveals where the bars really end, without waiting.
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(meter, Frame(top: 40, bottom: 40), times: 1));

        // A dark scene afterwards never widens it again.
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(meter, Frame(top: 70, bottom: 70)));
    }

    [Fact]
    public void Black_frames_say_nothing()
    {
        var meter = new BlackBarMeter();

        Assert.Equal(default, Feed(meter, Frame(picture: 0), times: 30));
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(meter, Frame(top: 40, bottom: 40)));
    }

    [Fact]
    public void Black_on_one_side_only_is_not_a_bar()
    {
        Assert.Equal(default, Feed(new BlackBarMeter(), Frame(top: 60), times: 30));
        Assert.Equal(default, Feed(new BlackBarMeter(), Frame(top: 60, bottom: 20), times: 30));
    }

    [Fact]
    public void Black_deeper_than_a_bar_could_be_is_a_dark_scene()
    {
        // A small title on a black screen: over a quarter of the frame on every side.
        Assert.Equal(default, Feed(new BlackBarMeter(), Frame(top: 120, bottom: 120, left: 200, right: 200), times: 30));
    }

    [Fact]
    public void Bars_too_thin_to_notice_stay()
    {
        Assert.Equal(default, Feed(new BlackBarMeter(), Frame(top: 2, bottom: 2), times: 30));
    }

    [Fact]
    public void Bars_a_line_apart_still_count_and_each_side_gets_its_own_cut()
    {
        Assert.Equal(new BlackBars(41, 42, 0, 0), Feed(new BlackBarMeter(), Frame(top: 40, bottom: 41)));
    }

    [Fact]
    public void Compressed_black_counts_as_black()
    {
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(new BlackBarMeter(), Frame(top: 40, bottom: 40, black: 18)));
    }

    [Fact]
    public void Haze_and_specks_in_a_bar_next_to_the_picture_do_not_move_its_edge()
    {
        // What a compressed bar really looks like in its last lines before a bright picture.
        var frame = Frame(top: 40, bottom: 40);
        foreach (var y in new[] { 36, 37, 38, 39, H - 40, H - 39, H - 38, H - 37 })
        {
            for (var x = 0; x < W; x++)
            {
                var level = (byte)(x % 156 == 6 ? 150 : 10);
                var at = (y * W + x) * 4;
                frame[at] = frame[at + 1] = frame[at + 2] = level;
            }
        }

        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(new BlackBarMeter(), frame));
    }

    [Fact]
    public void Grey_bars_are_part_of_the_picture()
    {
        Assert.Equal(default, Feed(new BlackBarMeter(), Frame(top: 40, bottom: 40, black: 60), times: 30));
    }

    [Theory]
    [InlineData(0)] // blue, the darkest colour by luma: a laser on a black stage is not black
    [InlineData(1)] // green
    [InlineData(2)] // red
    public void A_line_lit_in_any_single_colour_is_picture(int channel)
    {
        // Black everywhere, except one line under the top bar and one above the bottom bar.
        var frame = Frame(picture: 0);
        for (var x = 0; x < W; x++)
        {
            frame[(40 * W + x) * 4 + channel] = 255;
            frame[((H - 41) * W + x) * 4 + channel] = 255;
        }

        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(new BlackBarMeter(), frame));
    }

    [Fact]
    public void A_new_frame_size_starts_over()
    {
        var meter = new BlackBarMeter();
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(meter, Frame(top: 40, bottom: 40)));

        meter.Measure(Frame(width: 480, height: 360), 480, 360);

        Assert.Equal(default, meter.Bars);
    }

    [Fact]
    public void Reset_forgets_the_last_clip()
    {
        var meter = new BlackBarMeter();
        Assert.Equal(default, Feed(meter, Frame(), times: 30));

        meter.Reset();

        // The last clip reached the edges; this one has bars, and must get its cut.
        Assert.Equal(new BlackBars(41, 41, 0, 0), Feed(meter, Frame(top: 40, bottom: 40)));

        meter.Reset();

        Assert.Equal(default, meter.Bars);
    }

    [Fact]
    public void A_buffer_smaller_than_the_frame_is_ignored()
    {
        var meter = new BlackBarMeter();

        for (var i = 0; i < EnoughFrames; i++) meter.Measure(new byte[100], W, H);
        meter.Measure(Frame(), 0, 0);

        Assert.Equal(default, meter.Bars);
    }
}
