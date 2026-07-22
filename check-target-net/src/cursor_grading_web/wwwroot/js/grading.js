"use strict";

(function () {
    var connection = null;
    var logEntries = [];
    var lastJobCost = 0;
    var lastScrapeFails = 0;
    // "all" | "unable" | "scrape_non403"
    var logFilter = "all";
    // null = arrival (newest first), "asc", "desc"
    var reasonSort = null;

    function formatUsd(amount) {
        var n = Number(amount);
        if (!isFinite(n)) n = 0;
        return "$" + n.toFixed(6);
    }

    function escapeHtml(text) {
        return String(text == null ? "" : text)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function getGradeBadgeClass(grade) {
        switch (grade) {
            case "GOOD": return "bg-success";
            case "MAYBE": return "bg-warning text-dark";
            case "UNABLE": return "bg-danger";
            default: return "bg-secondary";
        }
    }

    function updateSpend(jobCost) {
        lastJobCost = Number(jobCost) || 0;
        var spendEl = document.getElementById("spend_total");
        if (spendEl) spendEl.textContent = formatUsd(lastJobCost);
        var finalEl = document.getElementById("spend_final");
        if (finalEl) finalEl.textContent = formatUsd(lastJobCost);
    }

    function updateScrapeFails(count) {
        lastScrapeFails = Number(count) || 0;
        var el = document.getElementById("scrape_fail_total");
        if (el) el.textContent = String(lastScrapeFails);
        var finalEl = document.getElementById("scrape_fail_final");
        if (finalEl) finalEl.textContent = String(lastScrapeFails);
    }

    function updateShowingCount(shown, total) {
        var el = document.getElementById("log_showing_count");
        if (el) el.textContent = "Showing " + shown + " of " + total;
    }

    function updateGradeSummary() {
        var good = 0;
        var maybe = 0;
        var unable = 0;
        for (var i = 0; i < logEntries.length; i++) {
            var g = logEntries[i].grade;
            if (g === "GOOD") good++;
            else if (g === "MAYBE") maybe++;
            else if (g === "UNABLE") unable++;
        }
        var goodEl = document.getElementById("summary_good");
        var maybeEl = document.getElementById("summary_maybe");
        var unableEl = document.getElementById("summary_unable");
        if (goodEl) goodEl.textContent = String(good);
        if (maybeEl) maybeEl.textContent = String(maybe);
        if (unableEl) unableEl.textContent = String(unable);
    }

    function updateReasonSortIndicator() {
        var el = document.getElementById("reason_sort_indicator");
        if (!el) return;
        if (reasonSort === "asc") el.textContent = "↑";
        else if (reasonSort === "desc") el.textContent = "↓";
        else el.textContent = "↕";
    }

    function isScrapeFailedNon403(entry) {
        var reason = entry.reason || "";
        if (entry.grade !== "UNABLE") return false;
        if (!/scrape failed/i.test(reason)) return false;
        if (/403/.test(reason)) return false;
        return true;
    }

    function setFilterButtonActive(btn, active, activeClass, inactiveClass) {
        if (!btn) return;
        btn.classList.remove(activeClass, inactiveClass);
        btn.classList.add(active ? activeClass : inactiveClass);
    }

    function updateFilterButtons() {
        setFilterButtonActive(
            document.getElementById("filter_all_btn"),
            logFilter === "all",
            "btn-secondary",
            "btn-outline-secondary");
        setFilterButtonActive(
            document.getElementById("filter_good_btn"),
            logFilter === "good",
            "btn-success",
            "btn-outline-success");
        setFilterButtonActive(
            document.getElementById("filter_unable_btn"),
            logFilter === "unable",
            "btn-danger",
            "btn-outline-danger");
        setFilterButtonActive(
            document.getElementById("filter_scrape_non403_btn"),
            logFilter === "scrape_non403",
            "btn-warning",
            "btn-outline-warning");
    }

    function getVisibleEntries() {
        var rows = logEntries.slice();

        if (logFilter === "good") {
            rows = rows.filter(function (e) { return e.grade === "GOOD"; });
        } else if (logFilter === "unable") {
            rows = rows.filter(function (e) { return e.grade === "UNABLE"; });
        } else if (logFilter === "scrape_non403") {
            rows = rows.filter(isScrapeFailedNon403);
        }

        if (reasonSort === "asc") {
            rows.sort(function (a, b) {
                return (a.reason || "").localeCompare(b.reason || "", undefined, { sensitivity: "base" });
            });
        } else if (reasonSort === "desc") {
            rows.sort(function (a, b) {
                return (b.reason || "").localeCompare(a.reason || "", undefined, { sensitivity: "base" });
            });
        } else {
            // Newest first (higher seq first)
            rows.sort(function (a, b) { return b.seq - a.seq; });
        }

        return rows;
    }

    function renderLog() {
        var tbody = document.getElementById("log_body");
        if (!tbody) return;

        var visible = getVisibleEntries();
        updateShowingCount(visible.length, logEntries.length);
        updateGradeSummary();

        var html = "";
        for (var i = 0; i < visible.length; i++) {
            var e = visible[i];
            html +=
                "<tr>" +
                "<td>" + e.seq + "</td>" +
                '<td><span class="badge ' + getGradeBadgeClass(e.grade) + '">' + escapeHtml(e.grade) + "</span></td>" +
                "<td>" + formatUsd(e.cost) + "</td>" +
                "<td>" + escapeHtml(e.reason) + "</td>" +
                "</tr>";
        }
        tbody.innerHTML = html;

        var container = document.getElementById("log_container");
        if (container && reasonSort === null) container.scrollTop = 0;
    }

    function appendLog(grade, reason, rowCost) {
        logEntries.push({
            seq: logEntries.length + 1,
            grade: grade || "",
            reason: reason || "",
            cost: rowCost
        });
        renderLog();
    }

    function setLogFilter(filter) {
        logFilter = filter;
        updateFilterButtons();
        renderLog();
    }

    function cycleReasonSort() {
        if (reasonSort === null) reasonSort = "asc";
        else if (reasonSort === "asc") reasonSort = "desc";
        else reasonSort = null;
        updateReasonSortIndicator();
        renderLog();
    }

    function updateProgress(completed, total, grade, reason, rowCost, jobCost, scrapeFails) {
        var bar = document.getElementById("progress_bar");
        var badge = document.getElementById("status_badge");

        if (bar && total > 0) {
            var pct = Math.round((completed / total) * 100);
            bar.style.width = pct + "%";
            bar.setAttribute("aria-valuenow", pct);
            bar.textContent = completed + " / " + total;
        }

        if (badge) {
            badge.textContent = "Running: " + completed + " of " + total + " graded";
            badge.className = "badge bg-primary mb-2";
        }

        updateSpend(jobCost);
        updateScrapeFails(scrapeFails);
        appendLog(grade, reason, rowCost);
        if (window.deepseekBalance && document.querySelector("[data-deepseek-balance]"))
            window.deepseekBalance.refresh(false);
    }

    function shutdownTrailingWork() {
        var cancelBtn = document.getElementById("cancel_btn");
        if (cancelBtn) {
            cancelBtn.disabled = true;
            cancelBtn.classList.add("disabled");
            cancelBtn.setAttribute("aria-disabled", "true");
        }

        if (!connection) return;

        try {
            connection.off("grading_progress");
            connection.off("job_complete");
            connection.off("job_error");
            connection.off("job_cancelled");
        } catch (e) { /* ignore */ }

        var conn = connection;
        connection = null;
        conn.stop().catch(function () { /* ignore */ });
    }

    function onComplete(outputPath) {
        var badge = document.getElementById("status_badge");
        var completeDiv = document.getElementById("complete_message");
        var downloadLink = document.getElementById("download_link");
        var bar = document.getElementById("progress_bar");

        if (badge) {
            badge.textContent = "Complete";
            badge.className = "badge bg-success mb-2";
        }

        if (bar) {
            bar.classList.remove("progress-bar-animated");
            bar.classList.remove("progress-bar-striped");
        }

        updateSpend(lastJobCost);
        updateScrapeFails(lastScrapeFails);
        if (window.deepseekBalance && document.querySelector("[data-deepseek-balance]"))
            window.deepseekBalance.refresh(true);

        if (completeDiv) {
            completeDiv.classList.remove("d-none");
        }

        if (downloadLink && outputPath) {
            var filename = outputPath.split(/[\\/]/).pop();
            downloadLink.href = "/uploads/" + filename;
        }

        shutdownTrailingWork();
    }

    function onError(errorMsg) {
        var badge = document.getElementById("status_badge");
        var errorDiv = document.getElementById("error_message");

        if (badge) {
            badge.textContent = "Error";
            badge.className = "badge bg-danger mb-2";
        }

        if (errorDiv) {
            errorDiv.textContent = "Error: " + (errorMsg || "Unknown error");
            errorDiv.classList.remove("d-none");
        }

        shutdownTrailingWork();
    }

    function onCancelled() {
        var badge = document.getElementById("status_badge");
        if (badge) {
            badge.textContent = "Cancelled";
            badge.className = "badge bg-warning text-dark mb-2";
        }

        var bar = document.getElementById("progress_bar");
        if (bar) {
            bar.classList.remove("progress-bar-animated");
            bar.classList.remove("progress-bar-striped");
        }

        shutdownTrailingWork();
    }

    async function startConnection() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl("/grading_hub")
            .withAutomaticReconnect()
            .build();

        connection.on("grading_progress", updateProgress);
        connection.on("job_complete", onComplete);
        connection.on("job_error", onError);
        connection.on("job_cancelled", onCancelled);

        try {
            await connection.start();
            console.log("SignalR connected");
        } catch (err) {
            console.error("SignalR connection failed:", err);
            document.getElementById("status_badge").textContent = "Connection failed";
        }
    }

    async function cancelJob() {
        try {
            var response = await fetch("/api/cancel_job", { method: "POST" });
            if (response.ok) {
                console.log("Cancellation requested");
            }
        } catch (err) {
            console.error("Failed to cancel:", err);
        }
    }

    document.addEventListener("DOMContentLoaded", function () {
        startConnection();
        updateFilterButtons();
        updateReasonSortIndicator();
        updateShowingCount(0, 0);

        var cancelBtn = document.getElementById("cancel_btn");
        if (cancelBtn) {
            cancelBtn.addEventListener("click", cancelJob);
        }

        var allBtn = document.getElementById("filter_all_btn");
        if (allBtn) allBtn.addEventListener("click", function () { setLogFilter("all"); });

        var goodBtn = document.getElementById("filter_good_btn");
        if (goodBtn) goodBtn.addEventListener("click", function () { setLogFilter("good"); });

        var unableBtn = document.getElementById("filter_unable_btn");
        if (unableBtn) unableBtn.addEventListener("click", function () { setLogFilter("unable"); });

        var scrapeBtn = document.getElementById("filter_scrape_non403_btn");
        if (scrapeBtn) scrapeBtn.addEventListener("click", function () { setLogFilter("scrape_non403"); });

        var reasonHeader = document.getElementById("reason_sort_header");
        if (reasonHeader) {
            reasonHeader.addEventListener("click", cycleReasonSort);
            reasonHeader.addEventListener("keydown", function (e) {
                if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    cycleReasonSort();
                }
            });
        }
    });
})();
