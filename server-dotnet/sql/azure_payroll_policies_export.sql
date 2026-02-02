--
-- PostgreSQL database dump
--

\restrict RG9PofbWQpISDFngIlTwvHTjsXsBzQdTT7u2Ze2kh62pkgWcdvP8ImQXO3GEnUf

-- Dumped from database version 16.11
-- Dumped by pg_dump version 18.0

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Data for Name: payroll_policies; Type: TABLE DATA; Schema: public; Owner: postgres
--

INSERT INTO public.payroll_policies (id, company_code, payload, created_at, updated_at, version, is_active) VALUES ('c8ac61b4-1287-4d7b-b394-6c86e73f664a', 'JP01', '{"dsl": {"law": {"source": "dataset", "dependencies": ["jp.health.standardMonthly", "jp.health.rate.employee", "jp.health.rate.employer", "jp.health.care.rate.employee", "jp.health.care.rate.employer", "jp.pension.standardMonthly", "jp.pension.rate.employee", "jp.pension.rate.employer", "jp.ei.rate.employee", "jp.ei.rate.employer"]}, "multipliers": {"holiday": 1.35, "overtime": 1.25, "lateNight": 1.25, "overtime60": 1.5}}, "code": "POL20260124-111817", "rules": [{"item": "BASE", "type": "earning", "formula": {"charRef": "employee.baseSalaryMonth"}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}}, {"item": "BASE", "type": "earning", "formula": {"hourlyPay": {"baseRef": "employee.hourlyRate", "hoursRef": "employee.workHours.totalHours", "directRate": true, "multiplier": 1}}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionContains": ["時給", "时薪", "hourly", "時間給"]}}, {"item": "OVERTIME_STD", "type": "earning", "formula": {"hourlyPay": {"baseRef": "employee.baseSalaryMonth", "hoursRef": "employee.workHours.overtimeHours", "multiplier": 1.25}}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}}, {"item": "ABSENCE_DEDUCT", "type": "deduction", "formula": {"hourlyPay": {"baseRef": "employee.baseSalaryMonth", "hoursRef": "employee.workHours.absenceHours", "multiplier": 1}}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}}, {"item": "HEALTH_INS", "type": "deduction", "formula": {"rate": "policy.law.health.rate", "_base": {"charRef": "employee.baseSalaryMonth"}}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}}, {"item": "PENSION", "type": "deduction", "formula": {"rate": "policy.law.pension.rate", "_base": {"charRef": "employee.baseSalaryMonth"}}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}}, {"item": "EMP_INS", "type": "deduction", "formula": {"rate": "policy.law.employment.rate", "_base": {"charRef": "employee.salaryTotal"}}, "rounding": {"method": "round_half_down", "precision": 0}}, {"item": "WHT", "type": "deduction", "formula": {"withholding": {"category": "monthly_ko"}}}, {"item": "CARE_INS", "type": "deduction", "formula": {"rate": "policy.law.care.rate", "_base": {"charRef": "employee.baseSalaryMonth"}}, "rounding": {"method": "round_half_down", "precision": 0}, "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}}], "nlText": "東京都協会けんぽの費率により、各社員の基本給を基礎として協会けんぽ／厚生年金の標準報酬月額表に照らし合わせ、該当区分の標準報酬月額と被保険者料率を自動で適用、社会保険・厚生年金・介護保険（40～64歳のみ）を算定し、事業区分は一般とする。雇用保険は雇用保険の一般事業の料率で計算する、基本給と各種手当の合計額は計算基礎とする。源泉徴収税は月額表甲欄を用い、社会保険と厚生年金控除後の金額を課税ベースとして求める。いずれも小数部が0.5円を超える場合のみ1円に切り上げ、0.5円以下は切り捨てる。\n\n勤怠データを基に日単位で所定労働時間を評価し、以下の割増賃金を支給する。時間単価は月額基本給を月間所定労働時間で除して算定する。\n・残業手当（item: OVERTIME_STD）：1日8時間を超え、月60時間以内の時間に対して時間単価の1.25倍を適用する。\n・60時間超残業手当（item: OVERTIME_60）：月60時間を超える残業時間に时间単価の1.5倍を適用する。\n・休日労働手当（item: HOLIDAY_PAY）：法定休日（自動祝日カレンダーによる判定）に勤務した时间へ时间単価の1.35倍を適用する。\n・深夜手当（item: LATE_NIGHT_PAY）：22:00～翌5:00に勤務した時間へ时间単価に25％加算した额を适用し、残業・休日と重複する場合は各割増を累積する。\nこれらの割増賃金は給与明細に独立した項目として表示し、会计上も給与手当（借方）／未払費用（貸方）にまとめて計上する。同じ貸方勘定コードで複数明細がある場合は合算する。\n\n欠勤控除（item: ABSENCE_DEDUCT）は所定労働时间を下回った不足時間に時間単価を掛けて計算し、給与明细では控除項目として表示する。会计处理は給与手当／未払費用的仕訳で借方を減額する形にまとめる。\n\n勤怠データが存在しない場合でも計算は続行し、残業・控除は实绩ゼロとして扱う。同時に「勤怠データが未登録のため、残業・控除は実績無しとして計算されています。」という警告を利用者へ表示する。\n\n会计伝票転記的规则は以下的通りとし、貸借勘定コードが同一の明细は一つにまとめる。\n・基本给、残業/欠勤控除および各種手当（交通手当、管理手当等）的合計：借方=給与手当、貸方=未払費用\n・社会保険：借方=未払費用、貸方=社会保険预り金\n・厚生年金：借方=未払費用、貸方=厚生年金预り金\n・雇用保険：借方=未払費用、貸方=雇用保険预り金\n・源泉徴収税：借方=未払費用、貸方=源泉所得税预り金\n\n・住民税（item: RESIDENT_TAX）：住民税管理で登録された特別徴収税額から、当月分的控除额を自動取得する。住民税的年度は6月から翌年5月まで。会计处理は未払費用／住民税预り金で仕訳する。", "version": "20260124-111817", "isActive": true}', '2026-01-24 02:18:17.308139+00', '2026-01-24 02:18:17.308139+00', '20260124-111817', true);


--
-- PostgreSQL database dump complete
--

\unrestrict RG9PofbWQpISDFngIlTwvHTjsXsBzQdTT7u2Ze2kh62pkgWcdvP8ImQXO3GEnUf

