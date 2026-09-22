-- Sequence supplying the numeric part of StaffId, incremented by one
-- for every registered staff member.
CREATE SEQUENCE [dbo].[StaffNumbers]
    AS INT
    START WITH 1
    INCREMENT BY 1;
