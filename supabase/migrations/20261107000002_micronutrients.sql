-- Phase 1, step 3: the rest of the nutrient catalogue, ahead of the USDA FoodData Central import (tools/importers).
--
-- The vitamins and minerals with an FDA Daily Value (21 CFR 101.9(c)(8)(iv)) that FoodData Central reports, then
-- alcohol and caffeine, in display order after the Nutrition Facts label set. Codes, units and order are
-- Health360.Domain/Nutrition/Nutrient.cs's (CanonicalUnitParityTests checks them). What each measures, where it
-- differs from the label's wording, is in docs/calculations.md (Nutrients): vitamin A in µg RAE, vitamin E as
-- alpha-tocopherol, vitamin K as phylloquinone, niacin preformed, folate in µg DFE.
--
-- Data only: nutrients is already in the publication and the reference stream, so clients receive the new rows.

insert into public.nutrients (code, unit, position) values
  ('vitamin_a', 'ug', 16),
  ('vitamin_c', 'mg', 17),
  ('vitamin_e', 'mg', 18),
  ('vitamin_k', 'ug', 19),
  ('thiamin', 'mg', 20),
  ('riboflavin', 'mg', 21),
  ('niacin', 'mg', 22),
  ('vitamin_b6', 'mg', 23),
  ('folate', 'ug', 24),
  ('vitamin_b12', 'ug', 25),
  ('biotin', 'ug', 26),
  ('pantothenic_acid', 'mg', 27),
  ('choline', 'mg', 28),
  ('phosphorus', 'mg', 29),
  ('iodine', 'ug', 30),
  ('magnesium', 'mg', 31),
  ('zinc', 'mg', 32),
  ('selenium', 'ug', 33),
  ('copper', 'mg', 34),
  ('manganese', 'mg', 35),
  ('molybdenum', 'ug', 36),
  ('alcohol', 'g', 37),
  ('caffeine', 'mg', 38);
