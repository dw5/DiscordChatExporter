using System.Threading.Tasks;
using AwesomeAssertions;
using DiscordChatExporter.Web.Services;
using Xunit;

namespace DiscordChatExporter.Web.Tests;

public class JobManagerTests
{
    [Fact]
    public void Keep_change_is_refused_while_that_runs_deletion_is_running()
    {
        var jobs = new JobManager();
        var gate = new TaskCompletionSource();

        // A deletion for run "r" that stays running until we release the gate.
        jobs.TryStart(JobKind.Deletion, "r", (_, _) => gate.Task).Should().NotBeNull();

        jobs.TryBeginKeepChange("r").Should().BeFalse();
        // A protection change on a different run is unaffected.
        jobs.TryBeginKeepChange("other").Should().BeTrue();
        jobs.EndKeepChange("other");

        gate.SetResult();
    }

    [Fact]
    public void Deletion_is_refused_while_a_keep_change_for_the_run_is_in_flight()
    {
        var jobs = new JobManager();

        jobs.TryBeginKeepChange("r").Should().BeTrue();

        // The deletion cannot be admitted while the protection is still committing.
        jobs.TryStart(JobKind.Deletion, "r", (_, _) => Task.CompletedTask).Should().BeNull();
        // A deletion for a different run is not blocked by run "r"'s pending protection.
        jobs.TryStart(JobKind.Deletion, "other", (_, _) => Task.CompletedTask)
            .Should()
            .NotBeNull();

        jobs.EndKeepChange("r");
    }

    [Fact]
    public void Deletion_is_admitted_once_the_keep_change_completes()
    {
        var jobs = new JobManager();

        jobs.TryBeginKeepChange("r").Should().BeTrue();
        jobs.EndKeepChange("r");

        jobs.TryStart(JobKind.Deletion, "r", (_, _) => Task.CompletedTask).Should().NotBeNull();
    }

    [Fact]
    public void A_pending_keep_change_blocks_admission_before_canStart_is_consulted()
    {
        var jobs = new JobManager();
        jobs.TryBeginKeepChange("r").Should().BeTrue();

        var canStartCalls = 0;
        var job = jobs.TryStart(
            JobKind.Deletion,
            "r",
            (_, _) => Task.CompletedTask,
            () =>
            {
                canStartCalls++;
                return true;
            }
        );

        job.Should().BeNull();
        // The pending protection must block admission BEFORE canStart runs, so canStart's plan
        // re-validation can assume no keep change is mid-commit for the run.
        canStartCalls.Should().Be(0);
        jobs.EndKeepChange("r");
    }

    [Fact]
    public void Admission_fails_when_canStart_rejects_the_now_stale_plan()
    {
        var jobs = new JobManager();

        // Stands in for the endpoint's in-lock re-validation finding the revision changed (e.g. a
        // keep commit landed between the pre-check and admission): canStart returns false.
        var job = jobs.TryStart(JobKind.Deletion, "r", (_, _) => Task.CompletedTask, () => false);

        job.Should().BeNull();
        // And a fresh admission with a valid guard still succeeds afterwards.
        jobs.TryStart(JobKind.Deletion, "r", (_, _) => Task.CompletedTask, () => true)
            .Should()
            .NotBeNull();
    }
}
