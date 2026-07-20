using System.Text;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class prompt_builder
{
    private const string who_we_are = """"
        We are Alliance Test Equipment (alliancetesteq.com), based in Webster, MA.
        We sell, rent, and lease used/refurbished electronic test & measurement
        equipment -- oscilloscopes, spectrum/signal analyzers, signal generators,
        meters, counters, power supplies, and wireless/optical test equipment, from
        brands like Agilent/HP/Keysight, Tektronix, Anritsu, Fluke, and Rohde &
        Schwarz. We also buy back customers' excess equipment and offer repair,
        calibration, and consignment.

        Our real customer is any company that has people on staff who need to
        measure, test, calibrate, or diagnose electronic hardware -- not just any
        company that happens to sell or use electronics.
        """";

    public string build(string website, string products, string about, string scraped_text, List<company_row>? examples = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are screening a B2B lead for outreach on our behalf.");
        sb.AppendLine();
        sb.AppendLine(who_we_are);
        sb.AppendLine();
        sb.AppendLine("Now research this prospect:");
        sb.AppendLine();
        sb.AppendLine($"Website: {website}");
        sb.AppendLine($"Stated primary products: {products}");
        sb.AppendLine($"Who they say they sell to: {about}");
        sb.AppendLine();
        sb.AppendLine("Here is the text content scraped from their website:");
        sb.AppendLine(scraped_text);
        sb.AppendLine();

        if (examples != null && examples.Count > 0)
        {
            sb.AppendLine("Here are a few examples of how similar companies were graded before, for calibration:");
            foreach (var ex in examples)
            {
                sb.AppendLine($"- Website: {ex.website}");
                sb.AppendLine($"  Products: {ex.products}");
                sb.AppendLine($"  About: {ex.about}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        sb.AppendLine("""
            Instructions:
            - Read the scraped website text carefully. Do not guess from the URL or company name alone.
            - The real question is NOT "is this company in electronics" -- it's "do they have
              people on staff who would actually use test & measurement equipment like an
              oscilloscope, signal generator, or power supply?" Look for signs of in-house
              engineering, R&D, manufacturing, calibration, diagnostics, or repair work on
              electronic hardware. A company that just resells or installs finished electronic
              products, with no visible engineering/test function, is NOT a good fit even if
              the word "electronics" is all over their site.
            - Grade the company using ONLY one of these three values:
              - "GOOD"   -> clear evidence of in-house engineering, R&D, manufacturing,
                            calibration, diagnostics, or repair work on electronic hardware --
                            these are people who plausibly own or need test & measurement gear
              - "MAYBE"  -> electronics-adjacent, but unclear whether they do real in-house
                            engineering/test work, or they mainly resell/install/integrate
                            finished components without their own test function
              - "UNABLE" -> you could not load or meaningfully read the site (broken link,
                            times out, blocked, parked domain, etc.) -- OR the scraped content
                            was insufficient to make a determination
            - Keep the reason to one short sentence, and name the specific evidence you saw
              (e.g. "runs their own ADAS calibration lab" or "just resells finished units,
              no engineering team visible").

            Respond with ONLY this exact JSON shape and nothing else -- no markdown, no code
            fences, no extra commentary:
            {"grade": "GOOD", "reason": "one short sentence"}
            """);

        return sb.ToString();
    }
}