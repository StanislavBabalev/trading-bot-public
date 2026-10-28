# Validation summary

0 of 18 candidates pass every hard gate. Trials: 5883 registered, 5732 distinct return series, 639 effective (Li & Ji eigenvalue count). PBO of the whole search (CSCV, 5731 configurations): 0.50.

| strategy | net % | Sharpe | PF | trades/yr | DSR (eff.) | DSR (raw) | plateau | boot. Sharpe p05 | boot. PF p05 | 2x slip net % | 2x comm net % | MinTRL days | failed hard gates |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| funding-fade-wide | 21.3 | 0.98 | 1.37 | 66 | 0.000 | 0.000 | 0.83 | 0.03 | 0.98 | 19.8 | 18.4 | 983 | deflated_sharpe, pbo, bootstrap_pf_p05 |
| crowding-filtered-breakout | 15.9 | 0.85 | 1.41 | 48 | 0.000 | 0.000 | 0.99 | -0.16 | 0.88 | 15.1 | 14.4 | 1262 | deflated_sharpe, pbo, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record, trades_per_year |
| funding-crowding-fade | 10.5 | 0.61 | 1.23 | 58 | 0.000 | 0.000 | 0.59 | -0.41 | 0.84 | 9.2 | 8.1 | 2573 | deflated_sharpe, pbo, plateau_score, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| max-min-daily | 7.3 | 0.60 | 1.24 | 69 | 0.000 | 0.000 | 0.97 | -0.51 | 0.78 | 6.3 | 5.3 | 2573 | deflated_sharpe, pbo, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| cipher-a-ribbon | 23.7 | 0.57 | 1.22 | 73 | 0.000 | 0.000 | 0.97 | -0.48 | 0.75 | 20.5 | 18.1 | 2908 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| donchian-ensemble-trend | 8.9 | 0.48 | 1.23 | 42 | 0.000 | 0.000 | 0.76 | -0.52 | 0.71 | 8.2 | 7.6 | 4060 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record, trades_per_year |
| supertrend | 28.6 | 0.46 | 1.14 | 109 | 0.000 | 0.000 | 0.27 | -0.55 | 0.79 | 18.8 | 11.1 | 4453 | deflated_sharpe, pbo, plateau_score, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| impulse-bar-continuation | 17.6 | 0.43 | 1.07 | 154 | 0.000 | 0.000 | 0.58 | -0.55 | 0.89 | 4.6 | -6.9 | 5067 | deflated_sharpe, pbo, plateau_score, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record |
| keltner-breakout | 20.8 | 0.41 | 1.08 | 166 | 0.000 | 0.000 | 0.93 | -0.63 | 0.84 | 10.7 | 1.2 | 5582 | deflated_sharpe, pbo, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| funding-tilt | 7.7 | 0.37 | 1.12 | 75 | 0.000 | 0.000 | 0.46 | -0.60 | 0.81 | 6.3 | 4.9 | 6926 | deflated_sharpe, pbo, plateau_score, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| donchian-breakout | 16.6 | 0.36 | 1.10 | 91 | 0.000 | 0.000 | 0.49 | -0.67 | 0.76 | 11.6 | 6.6 | 7130 | deflated_sharpe, pbo, plateau_score, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| ichimoku-cloud | 10.2 | 0.34 | 1.10 | 94 | 0.000 | 0.000 | 0.66 | -0.77 | 0.74 | 6.7 | 3.8 | 8337 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| funding-carry-tilt | 2.9 | 0.26 | 1.11 | 37 | 0.000 | 0.000 | 0.65 | -0.75 | 0.67 | 2.4 | 1.9 | 14869 | deflated_sharpe, pbo, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record, trades_per_year |
| ts-momentum | 9.9 | 0.25 | 1.04 | 155 | 0.000 | 0.000 | 0.46 | -0.79 | 0.77 | 1.9 | -6.4 | 15461 | deflated_sharpe, pbo, plateau_score, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record |
| nr-compression-breakout | 5.5 | 0.24 | 1.06 | 73 | 0.000 | 0.000 | 0.03 | -0.79 | 0.79 | 1.9 | -1.3 | 16379 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, regime_concentration, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record |
| turmoil-reversal | 2.4 | 0.16 | 1.05 | 42 | 0.000 | 0.000 | 0.11 | -0.82 | 0.74 | 0.9 | -0.6 | 39799 | deflated_sharpe, pbo, plateau_score, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record, trades_per_year |
| vol-regime-switch | 1.7 | 0.12 | 1.01 | 145 | 0.000 | 0.000 | 0.13 | -0.89 | 0.82 | -8.5 | -17.9 | 68492 | deflated_sharpe, pbo, plateau_score, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| tsmom-vote | 1.3 | 0.10 | 1.03 | 57 | 0.000 | 0.000 | 0.07 | -0.89 | 0.61 | 0.5 | -0.3 | 105569 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record |

Hard gates failed (number of candidates): deflated_sharpe 18, pbo 18, bootstrap_pf_p05 18, bootstrap_sharpe_p05 17, min_track_record 17, regime_losses 14, plateau_score 10, stress_commission_2x 6, plateau_edge 6, trades_per_year 4, stress_slippage_2x 1, regime_concentration 1
