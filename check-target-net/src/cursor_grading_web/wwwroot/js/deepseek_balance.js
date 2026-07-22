"use strict";

(function (global) {
    var lastFetchAt = 0;
    var inFlight = null;
    var lastProvider = null;

    function formatMoney(amount, currency) {
        var n = Number(amount);
        if (!isFinite(n)) n = 0;
        var code = (currency || "USD").toUpperCase();
        var symbol = code === "USD" ? "$" : (code === "CNY" ? "¥" : code + " ");
        return symbol + n.toFixed(2) + " " + code;
    }

    function currentProvider() {
        var box = document.querySelector("[data-llm-provider]");
        return (box && box.getAttribute("data-llm-provider")) || "deepseek";
    }

    function applyBalance(data) {
        var els = document.querySelectorAll("[data-deepseek-balance]");
        if (!els.length) return;

        els.forEach(function (el) {
            if (!data || !data.ok) {
                el.textContent = "unavailable";
                el.classList.add("text-muted");
                return;
            }

            el.textContent = formatMoney(data.total_balance, data.currency);
            el.classList.remove("text-muted");
            if (data.is_available === false) {
                el.classList.add("text-danger");
            } else {
                el.classList.remove("text-danger");
            }
        });

        var detailEls = document.querySelectorAll("[data-deepseek-balance-detail]");
        detailEls.forEach(function (el) {
            if (!data || !data.ok) {
                el.textContent = "";
                return;
            }
            el.textContent =
                "Topped-up " + formatMoney(data.topped_up_balance, data.currency) +
                " · Granted " + formatMoney(data.granted_balance, data.currency);
        });
    }

    function fetchBalance(force, provider) {
        provider = provider || currentProvider();
        var now = Date.now();
        if (!force && provider === lastProvider && now - lastFetchAt < 5000 && inFlight) {
            return inFlight;
        }
        if (!force && provider === lastProvider && now - lastFetchAt < 5000) {
            return Promise.resolve(null);
        }

        lastFetchAt = now;
        lastProvider = provider;
        inFlight = fetch("/api/deepseek_balance?provider=" + encodeURIComponent(provider))
            .then(function (r) { return r.json(); })
            .then(function (data) {
                applyBalance(data);
                return data;
            })
            .catch(function () {
                applyBalance(null);
                return null;
            })
            .finally(function () {
                inFlight = null;
            });

        return inFlight;
    }

    global.deepseekBalance = {
        refresh: fetchBalance,
        formatMoney: formatMoney
    };

    document.addEventListener("DOMContentLoaded", function () {
        if (document.querySelector("[data-deepseek-balance]") &&
            document.querySelector("[data-llm-supports-balance='true']")) {
            fetchBalance(true);
        }
    });
})(window);
