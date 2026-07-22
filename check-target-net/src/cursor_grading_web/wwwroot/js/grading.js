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
    var detailModal = null;

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

    function displayOrDash(value) {
        var s = String(value == null ? "" : value).trim();
        return s ? s : "—";
    }

    function normalizeWebsiteHref(url) {
        var s = String(url == null ? "" : url).trim();
        if (!s) return "";
        if (!/^https?:\/\//i.test(s)) s = "https://" + s;
        return s;
    }

    function getGradeBadgeClass(grade) {
        switch (grade) {
            case "GOOD": return "bg-success";
            case "MAYBE": return "bg-warning text-dark";
            case "UNABLE": return "bg-danger";
            default: return "bg-secondary";
        }
    }

    function isFailureEntry(entry) {
        return entry && entry.grade === "UNABLE";
    }

    function explainFailure(entry) {
        var reason = entry.reason || "";
        var lower = reason.toLowerCase();

        if (/scrape failed/i.test(reason) && /429|too many requests/i.test(reason)) {
            return {
                summary: "Website rate-limited the scrape",
                explanation:
                    "The target site (or a WAF/CDN in front of it) returned HTTP 429. " +
                    "That means too many requests were received from this IP in a short time, " +
                    "so the page content was never retrieved and the LLM could not grade it.",
                suggestions: [
                    "Lower Max Workers so fewer sites are scraped at once.",
                    "Re-run later for this row; many 429s are temporary.",
                    "If many rows fail this way, add scrape retries with backoff."
                ]
            };
        }

        if (/scrape failed/i.test(reason) && /403|forbidden/i.test(reason)) {
            return {
                summary: "Website blocked the scrape",
                explanation:
                    "The site returned HTTP 403 Forbidden. It refused to serve the page to this scraper " +
                    "(bot protection, geo rules, or login-only content).",
                suggestions: [
                    "Open the website manually to confirm it loads in a browser.",
                    "Some sites cannot be scraped reliably; leave as UNABLE or grade manually."
                ]
            };
        }

        if (/ssl|tls|certificate/i.test(lower)) {
            return {
                summary: "HTTPS / SSL connection failed",
                explanation:
                    "The scraper could not complete a secure connection to the website. " +
                    "No HTTP page was returned. Common causes: expired or mismatched certificate, " +
                    "TLS version mismatch, broken HTTPS setup, or a proxy/WAF interrupting the handshake.",
                suggestions: [
                    "Open the URL in a browser and check for certificate warnings.",
                    "Verify the spreadsheet URL (www vs non-www, http vs https).",
                    "If the site’s certificate is broken, UNABLE is expected until the site is fixed."
                ]
            };
        }

        if (/timed out|timeout/i.test(lower)) {
            return {
                summary: "Request timed out",
                explanation:
                    "The scrape did not finish within the allowed time. The site may be slow, unreachable, " +
                    "or hanging during the connection.",
                suggestions: [
                    "Try the URL in a browser to see if it loads slowly.",
                    "Re-run this row later; timeouts are often transient."
                ]
            };
        }

        if (/scrape failed/i.test(reason) && /network error/i.test(reason)) {
            return {
                summary: "Network error while scraping",
                explanation:
                    "The HTTP client failed before getting a usable page response. " +
                    "This can be DNS failure, connection reset, TLS problems, or the host being unreachable.",
                suggestions: [
                    "Check that the website URL in the spreadsheet is correct.",
                    "Confirm the host resolves and loads outside this tool."
                ]
            };
        }

        if (/scrape failed/i.test(reason)) {
            return {
                summary: "Website scrape failed",
                explanation:
                    "The tool could not extract usable page text from this company’s website, " +
                    "so it marked the row UNABLE instead of guessing from the URL alone.",
                suggestions: [
                    "Read the technical reason below for the exact HTTP/network detail.",
                    "Open the site manually; if it works only after JS challenges, scraping may not be possible."
                ]
            };
        }

        if (/429|rate limit/i.test(lower) && /(deepseek|gemini|http 429)/i.test(lower)) {
            return {
                summary: "LLM provider rate-limited the request",
                explanation:
                    "The AI provider rejected the grading call with HTTP 429 (too many concurrent or too-fast requests). " +
                    "The website may have scraped successfully, but the model call failed.",
                suggestions: [
                    "Lower Max Workers.",
                    "Increase ramp interval so requests start more gradually.",
                    "Re-run failed rows after a short wait."
                ]
            };
        }

        if (/failed after/i.test(lower) || /httprequestexception/i.test(lower)) {
            return {
                summary: "LLM grading call failed",
                explanation:
                    "After scraping (if that succeeded), calling the LLM failed one or more times. " +
                    "See the technical reason for the provider status and message.",
                suggestions: [
                    "Check API key, model name, and account balance/quota.",
                    "Retry with fewer workers if the error looks like rate limiting."
                ]
            };
        }

        if (entry.grade === "UNABLE") {
            return {
                summary: "Marked UNABLE",
                explanation:
                    "The grader could not classify this company as GOOD or MAYBE. " +
                    "That may be intentional model judgment (weak fit / unclear site) " +
                    "or a technical failure described in the reason.",
                suggestions: [
                    "Read the technical reason and company fields below.",
                    "Open the website to decide if a manual override is needed."
                ]
            };
        }

        return {
            summary: "Grading result",
            explanation: "This row completed with the grade shown above.",
            suggestions: []
        };
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

    function findEntryBySeq(seq) {
        for (var i = 0; i < logEntries.length; i++) {
            if (logEntries[i].seq === seq) return logEntries[i];
        }
        return null;
    }

    function setText(id, value) {
        var el = document.getElementById(id);
        if (el) el.textContent = value;
    }

    function showResultDetail(entry) {
        if (!entry) return;

        var explained = explainFailure(entry);
        var badge = document.getElementById("detail_grade_badge");
        if (badge) {
            badge.className = "badge " + getGradeBadgeClass(entry.grade);
            badge.textContent = entry.grade || "—";
        }

        setText("detail_excel_row", entry.excelRow > 0 ? String(entry.excelRow) : "—");
        setText("detail_seq", String(entry.seq));
        setText("detail_cost", formatUsd(entry.cost));
        setText("detail_summary", explained.summary);
        setText("detail_explanation", explained.explanation);
        setText("detail_reason", entry.reason || "—");
        setText("detail_company", displayOrDash(entry.company));
        setText("detail_contact", displayOrDash(entry.contact));
        setText("detail_email", displayOrDash(entry.email));
        setText("detail_phone", displayOrDash(entry.phone));
        setText("detail_products", displayOrDash(entry.products));
        setText("detail_about", displayOrDash(entry.about));

        var websiteEl = document.getElementById("detail_website");
        var openBtn = document.getElementById("detail_open_website");
        var href = normalizeWebsiteHref(entry.website);
        if (websiteEl) {
            if (href) {
                websiteEl.innerHTML =
                    '<a href="' + escapeHtml(href) + '" target="_blank" rel="noopener noreferrer">' +
                    escapeHtml(entry.website) + "</a>";
            } else {
                websiteEl.textContent = "—";
            }
        }
        if (openBtn) {
            if (href) {
                openBtn.href = href;
                openBtn.classList.remove("d-none");
            } else {
                openBtn.classList.add("d-none");
            }
        }

        var suggestionsWrap = document.getElementById("detail_suggestions_wrap");
        var suggestionsList = document.getElementById("detail_suggestions");
        if (suggestionsWrap && suggestionsList) {
            suggestionsList.innerHTML = "";
            if (explained.suggestions && explained.suggestions.length) {
                for (var i = 0; i < explained.suggestions.length; i++) {
                    var li = document.createElement("li");
                    li.textContent = explained.suggestions[i];
                    suggestionsList.appendChild(li);
                }
                suggestionsWrap.classList.remove("d-none");
            } else {
                suggestionsWrap.classList.add("d-none");
            }
        }

        var title = document.getElementById("result_detail_title");
        if (title) {
            title.textContent = isFailureEntry(entry)
                ? "Failure details"
                : "Result details";
        }

        if (detailModal) detailModal.show();
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
            var clickable = isFailureEntry(e);
            var rowClass = clickable ? ' class="log-row-clickable"' : "";
            var attrs = clickable
                ? ' role="button" tabindex="0" data-seq="' + e.seq + '" title="Click for failure details"'
                : "";
            html +=
                "<tr" + rowClass + attrs + ">" +
                "<td>" + e.seq + "</td>" +
                '<td><span class="badge ' + getGradeBadgeClass(e.grade) + '">' + escapeHtml(e.grade) + "</span></td>" +
                "<td>" + formatUsd(e.cost) + "</td>" +
                "<td>" + escapeHtml(e.reason) +
                (clickable ? ' <span class="text-muted small">Details</span>' : "") +
                "</td>" +
                "</tr>";
        }
        tbody.innerHTML = html;

        var container = document.getElementById("log_container");
        if (container && reasonSort === null) container.scrollTop = 0;
    }

    function appendLog(grade, reason, rowCost, meta) {
        meta = meta || {};
        logEntries.push({
            seq: logEntries.length + 1,
            grade: grade || "",
            reason: reason || "",
            cost: rowCost,
            excelRow: Number(meta.excelRow) || 0,
            company: meta.company || "",
            website: meta.website || "",
            products: meta.products || "",
            about: meta.about || "",
            contact: meta.contact || "",
            email: meta.email || "",
            phone: meta.phone || ""
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

    function updateProgress(payload) {
        // Support both object payload (current) and legacy positional args if ever replayed.
        var completed, total, grade, reason, rowCost, jobCost, scrapeFails, meta;
        if (payload && typeof payload === "object" && !Array.isArray(payload) && "completed" in payload) {
            completed = payload.completed;
            total = payload.total;
            grade = payload.grade;
            reason = payload.reason;
            rowCost = payload.row_cost;
            jobCost = payload.job_cost;
            scrapeFails = payload.scrape_fails;
            meta = {
                excelRow: payload.excel_row,
                company: payload.company,
                website: payload.website,
                products: payload.products,
                about: payload.about,
                contact: payload.contact,
                email: payload.email,
                phone: payload.phone
            };
        } else {
            completed = arguments[0];
            total = arguments[1];
            grade = arguments[2];
            reason = arguments[3];
            rowCost = arguments[4];
            jobCost = arguments[5];
            scrapeFails = arguments[6];
            meta = {};
        }

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
        appendLog(grade, reason, rowCost, meta);
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

        var modalEl = document.getElementById("result_detail_modal");
        if (modalEl && window.bootstrap && bootstrap.Modal) {
            detailModal = bootstrap.Modal.getOrCreateInstance(modalEl);
        }

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

        var tbody = document.getElementById("log_body");
        if (tbody) {
            tbody.addEventListener("click", function (e) {
                var tr = e.target.closest("tr.log-row-clickable");
                if (!tr) return;
                var seq = Number(tr.getAttribute("data-seq"));
                showResultDetail(findEntryBySeq(seq));
            });
            tbody.addEventListener("keydown", function (e) {
                if (e.key !== "Enter" && e.key !== " ") return;
                var tr = e.target.closest("tr.log-row-clickable");
                if (!tr) return;
                e.preventDefault();
                var seq = Number(tr.getAttribute("data-seq"));
                showResultDetail(findEntryBySeq(seq));
            });
        }
    });
})();
