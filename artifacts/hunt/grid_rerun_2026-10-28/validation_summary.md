# Validation summary

0 of 16 candidates pass every hard gate. Trials: 2084 registered, 1976 distinct return series, 300 effective (Li & Ji eigenvalue count). PBO of the whole search (CSCV, 1976 configurations): 0.39.

| strategy | net % | Sharpe | PF | trades/yr | DSR (eff.) | DSR (raw) | plateau | boot. Sharpe p05 | boot. PF p05 | 2x slip net % | 2x comm net % | MinTRL days | failed hard gates |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| impulse-bar-continuation | 73.7 | 1.78 | 1.55 | 97 | 0.622 | 0.443 | 0.88 | 0.89 | 1.26 | 64.8 | 57.7 | 248 | deflated_sharpe, pbo, plateau_edge |
| keltner-breakout | 63.0 | 1.09 | 1.34 | 173 | 0.158 | 0.074 | 1.00 | 0.06 | 0.97 | 52.4 | 44.2 | 697 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_pf_p05 |
| vol-regime-switch | 59.2 | 1.09 | 1.38 | 85 | 0.155 | 0.072 | 0.99 | 0.17 | 1.05 | 50.8 | 43.5 | 690 | deflated_sharpe, pbo, plateau_edge |
| ichimoku-cloud | 28.6 | 1.05 | 1.58 | 59 | 0.145 | 0.068 | 0.86 | 0.05 | 0.92 | 26.7 | 25.3 | 798 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_pf_p05 |
| crowding-filtered-breakout | 12.3 | 1.02 | 1.50 | 54 | 0.133 | 0.062 | 1.00 | 0.02 | 0.95 | 11.7 | 11.2 | 836 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_pf_p05 |
| funding-tilt | 21.3 | 0.98 | 1.37 | 66 | 0.127 | 0.060 | 0.83 | 0.03 | 0.98 | 19.8 | 18.4 | 983 | deflated_sharpe, pbo, plateau_edge, bootstrap_pf_p05 |
| donchian-breakout | 58.7 | 0.98 | 1.50 | 58 | 0.115 | 0.051 | 1.00 | -0.07 | 0.87 | 54.8 | 51.8 | 892 | deflated_sharpe, pbo, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05 |
| ts-momentum | 50.7 | 0.88 | 1.28 | 157 | 0.087 | 0.037 | 0.69 | -0.17 | 0.91 | 42.8 | 36.8 | 1158 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| max-min-daily | 14.4 | 0.76 | 1.33 | 53 | 0.062 | 0.026 | 0.99 | -0.33 | 0.87 | 13.2 | 12.2 | 1659 | deflated_sharpe, pbo, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| supertrend | 57.4 | 0.74 | 1.25 | 124 | 0.057 | 0.023 | 0.33 | -0.20 | 0.92 | 44.1 | 33.6 | 1650 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| cipher-a-ribbon | 47.5 | 0.72 | 1.20 | 132 | 0.053 | 0.021 | 0.95 | -0.33 | 0.87 | 39.0 | 30.4 | 1764 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| turmoil-reversal | 14.7 | 0.64 | 1.19 | 66 | 0.042 | 0.016 | 0.23 | -0.32 | 0.91 | 11.9 | 9.3 | 2407 | deflated_sharpe, pbo, plateau_score, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| donchian-ensemble-trend | 7.5 | 0.59 | 1.24 | 74 | 0.033 | 0.012 | 1.00 | -0.40 | 0.81 | 6.7 | 6.0 | 2759 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| funding-carry-tilt | 16.3 | 0.56 | 1.21 | 57 | 0.030 | 0.011 | 0.81 | -0.43 | 0.84 | 14.3 | 12.8 | 3068 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| tsmom-vote | 13.0 | 0.45 | 1.18 | 60 | 0.019 | 0.006 | 0.41 | -0.59 | 0.75 | 11.0 | 9.4 | 4751 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| nr-compression-breakout | 11.8 | 0.35 | 1.10 | 62 | 0.011 | 0.003 | 0.30 | -0.70 | 0.78 | 6.9 | 2.1 | 7389 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |

Hard gates failed (number of candidates): deflated_sharpe 16, pbo 16, bootstrap_pf_p05 14, plateau_edge 14, regime_losses 10, bootstrap_sharpe_p05 10, min_track_record 9, plateau_score 4
