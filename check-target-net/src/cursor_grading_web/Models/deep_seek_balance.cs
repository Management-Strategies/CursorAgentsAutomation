namespace cursor_grading_web.Models;

public record deep_seek_balance(
    bool is_available,
    string currency,
    decimal total_balance,
    decimal granted_balance,
    decimal topped_up_balance
);
