# Validation summary

0 of 38 candidates pass every hard gate. Trials: 5616 registered, 5473 distinct return series, 618 effective (Li & Ji eigenvalue count). PBO of the whole search (CSCV, 5472 configurations): 0.47.

| strategy | net % | Sharpe | PF | trades/yr | DSR (eff.) | DSR (raw) | plateau | boot. Sharpe p05 | boot. PF p05 | 2x slip net % | 2x comm net % | MinTRL days | failed hard gates |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| impulse-bar-continuation | 73.7 | 1.78 | 1.55 | 97 | 0.041 | 0.019 | 0.88 | 0.89 | 1.26 | 64.8 | 57.7 | 248 | deflated_sharpe, pbo, plateau_edge |
| keltner-breakout | 63.0 | 1.09 | 1.34 | 173 | 0.001 | 0.000 | 1.00 | 0.06 | 0.97 | 52.4 | 44.2 | 697 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_pf_p05 |
| vol-regime-switch | 59.2 | 1.09 | 1.38 | 85 | 0.001 | 0.000 | 0.99 | 0.17 | 1.05 | 50.8 | 43.5 | 690 | deflated_sharpe, pbo, plateau_edge |
| ichimoku-cloud | 28.6 | 1.05 | 1.58 | 59 | 0.001 | 0.000 | 0.86 | 0.05 | 0.92 | 26.7 | 25.3 | 798 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_pf_p05 |
| crowding-filtered-breakout | 12.3 | 1.02 | 1.50 | 54 | 0.001 | 0.000 | 1.00 | 0.02 | 0.95 | 11.7 | 11.2 | 836 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_pf_p05 |
| mtf-trend-pullback | 31.7 | 0.99 | 1.38 | 72 | 0.001 | 0.000 | 0.95 | 0.03 | 0.98 | 29.1 | 26.9 | 950 | deflated_sharpe, pbo, plateau_edge, bootstrap_pf_p05 |
| funding-tilt | 21.3 | 0.98 | 1.37 | 66 | 0.001 | 0.000 | 0.83 | 0.03 | 0.98 | 19.8 | 18.4 | 983 | deflated_sharpe, pbo, plateau_edge, bootstrap_pf_p05 |
| donchian-breakout | 58.7 | 0.98 | 1.50 | 58 | 0.001 | 0.000 | 1.00 | -0.07 | 0.87 | 54.8 | 51.8 | 892 | deflated_sharpe, pbo, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05 |
| ema-cross-control | 22.1 | 0.88 | 1.28 | 74 | 0.001 | 0.000 | 0.48 | -0.08 | 0.96 | 20.5 | 18.9 | 1239 | deflated_sharpe, pbo, plateau_score, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| ts-momentum | 50.7 | 0.88 | 1.28 | 157 | 0.001 | 0.000 | 0.69 | -0.17 | 0.91 | 42.8 | 36.8 | 1158 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| ema-cross-atr | 31.1 | 0.83 | 1.49 | 56 | 0.000 | 0.000 | 1.00 | -0.27 | 0.72 | 28.4 | 27.0 | 1268 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| dca-rsi-long | 4.0 | 0.79 | 1.94 | 58 | 0.001 | 0.000 | 0.14 | -0.13 | 0.99 | 3.8 | 3.6 | 1620 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| dca-grid-long | 3.7 | 0.76 | 1.49 | 420 | 0.001 | 0.000 | 0.56 | -0.16 | 0.89 | 3.3 | 2.6 | 1815 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| max-min-daily | 14.4 | 0.76 | 1.33 | 53 | 0.000 | 0.000 | 0.99 | -0.33 | 0.87 | 13.2 | 12.2 | 1659 | deflated_sharpe, pbo, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| supertrend | 57.4 | 0.74 | 1.25 | 124 | 0.000 | 0.000 | 0.33 | -0.20 | 0.92 | 44.1 | 33.6 | 1650 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| cipher-b-confluence | 21.8 | 0.73 | 1.32 | 51 | 0.000 | 0.000 | 0.61 | -0.29 | 0.88 | 19.3 | 17.2 | 1678 | deflated_sharpe, pbo, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| cipher-a-ribbon | 47.5 | 0.72 | 1.20 | 132 | 0.000 | 0.000 | 0.95 | -0.33 | 0.87 | 39.0 | 30.4 | 1764 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| failed-breakout-fade | 16.9 | 0.71 | 1.19 | 85 | 0.000 | 0.000 | 0.81 | -0.26 | 0.94 | 11.7 | 6.8 | 1945 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| volume-conditioned-reversal | 21.8 | 0.69 | 1.23 | 73 | 0.000 | 0.000 | 0.69 | -0.32 | 0.86 | 19.3 | 17.2 | 1971 | deflated_sharpe, pbo, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| turmoil-reversal | 14.7 | 0.64 | 1.19 | 66 | 0.000 | 0.000 | 0.23 | -0.32 | 0.91 | 11.9 | 9.3 | 2407 | deflated_sharpe, pbo, plateau_score, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| dca-trend-long | 2.2 | 0.62 | 1.98 | 352 | 0.000 | 0.000 | 0.38 | -0.27 | 0.83 | 1.8 | 1.2 | 2888 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| donchian-ensemble-trend | 7.5 | 0.59 | 1.24 | 74 | 0.000 | 0.000 | 1.00 | -0.40 | 0.81 | 6.7 | 6.0 | 2759 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| round-number-bounce | 22.1 | 0.57 | 1.17 | 68 | 0.000 | 0.000 | 0.14 | -0.43 | 0.87 | 15.8 | 10.5 | 2795 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| funding-carry-tilt | 16.3 | 0.56 | 1.21 | 57 | 0.000 | 0.000 | 0.81 | -0.43 | 0.84 | 14.3 | 12.8 | 3068 | deflated_sharpe, pbo, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| noise-area-momentum | 23.9 | 0.55 | 1.13 | 196 | 0.000 | 0.000 | 0.07 | -0.39 | 0.89 | 7.5 | -6.0 | 2624 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record |
| tsmom-vote | 13.0 | 0.45 | 1.18 | 60 | 0.000 | 0.000 | 0.41 | -0.59 | 0.75 | 11.0 | 9.4 | 4751 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| squeeze-momentum | 8.9 | 0.41 | 1.12 | 127 | 0.000 | 0.000 | 0.44 | -0.60 | 0.84 | 4.0 | -0.6 | 5324 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_commission_2x, min_track_record |
| nr-compression-breakout | 11.8 | 0.35 | 1.10 | 62 | 0.000 | 0.000 | 0.30 | -0.70 | 0.78 | 6.9 | 2.1 | 7389 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| efficiency-regime-switch | 16.5 | 0.32 | 1.05 | 203 | 0.000 | 0.000 | 0.09 | -0.70 | 0.84 | -3.5 | -19.6 | 9364 | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| cipher-b-dots | 6.5 | 0.25 | 1.07 | 80 | 0.000 | 0.000 | 0.05 | -0.82 | 0.77 | 3.3 | 0.2 | 15949 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, min_track_record |
| dca-grid-short | 2.0 | 0.17 | 1.04 | 306 | 0.000 | 0.000 | 0.03 | -0.76 | 0.83 | -1.4 | -1.3 | 32622 | deflated_sharpe, pbo, plateau_score, plateau_edge, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| session-momentum | -2.7 | -0.09 | 0.96 | 66 | 0.000 | 0.000 | 0.00 | -1.13 | 0.70 | -5.8 | -8.8 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| cipher-b-divergence | -7.6 | -0.18 | 0.92 | 65 | 0.000 | 0.000 | 0.00 | -1.25 | 0.65 | -12.5 | -17.5 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| volume-climax-reversal | -9.5 | -0.20 | 0.93 | 88 | 0.000 | 0.000 | 0.00 | -1.33 | 0.70 | -15.2 | -20.8 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| trend-pullback-rsi | -2.3 | -0.21 | 0.92 | 52 | 0.000 | 0.000 | 0.01 | -1.08 | 0.66 | -4.4 | -6.5 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| cipher-b-sommi-flag | -6.2 | -0.24 | 0.91 | 60 | 0.000 | 0.000 | 0.38 | -1.35 | 0.60 | -10.5 | -14.8 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| cascade-fade | -23.3 | -0.75 | 0.82 | 70 | 0.000 | 0.000 | 0.00 | -1.80 | 0.61 | -28.2 | -33.1 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |
| bollinger-reversion | -29.7 | -0.86 | 0.88 | 210 | 0.000 | 0.000 | 0.00 | -1.80 | 0.77 | -37.0 | -44.2 | inf | deflated_sharpe, pbo, plateau_score, plateau_edge, regime_losses, bootstrap_sharpe_p05, bootstrap_pf_p05, stress_slippage_2x, stress_commission_2x, min_track_record |

Hard gates failed (number of candidates): deflated_sharpe 38, pbo 38, bootstrap_pf_p05 36, plateau_edge 35, bootstrap_sharpe_p05 31, min_track_record 30, plateau_score 21, regime_losses 20, stress_commission_2x 11, stress_slippage_2x 9
