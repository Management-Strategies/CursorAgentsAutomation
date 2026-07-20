"use strict";

(function () {
    var connection = null;
    var logCount = 0;

    function getGradeBadgeClass(grade) {
        switch (grade) {
            case "GOOD": return "bg-success";
            case "MAYBE": return "bg-warning text-dark";
            case "UNABLE": return "bg-danger";
            default: return "bg-secondary";
        }
    }

    function appendLog(grade, reason) {
        logCount++;
        var tbody = document.getElementById("log_body");
        if (!tbody) return;

        var tr = document.createElement("tr");
        tr.innerHTML =
            '<td>' + logCount + '</td>' +
            '<td><span class="badge ' + getGradeBadgeClass(grade) + '">' + grade + '</span></td>' +
            '<td>' + (reason || "") + '</td>';
        tbody.insertBefore(tr, tbody.firstChild);

        // Auto-scroll to top
        var container = document.getElementById("log_container");
        if (container) container.scrollTop = 0;
    }

    function updateProgress(completed, total, grade, reason) {
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

        appendLog(grade, reason);
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

        if (completeDiv) {
            completeDiv.classList.remove("d-none");
        }

        if (downloadLink && outputPath) {
            var filename = outputPath.split(/[\\/]/).pop();
            downloadLink.href = "/uploads/" + filename;
        }
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
    }

    function onCancelled() {
        var badge = document.getElementById("status_badge");
        if (badge) {
            badge.textContent = "Cancelled";
            badge.className = "badge bg-warning text-dark mb-2";
        }
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

    // Initialize on page load
    document.addEventListener("DOMContentLoaded", function () {
        startConnection();

        var cancelBtn = document.getElementById("cancel_btn");
        if (cancelBtn) {
            cancelBtn.addEventListener("click", cancelJob);
        }
    });
})();