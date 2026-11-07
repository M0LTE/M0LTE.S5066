namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// An internal primitive on the dispatcher FIFO (core-architecture.md 3.5 rule 2, 4.1). The D_
/// and C_ primitives of Tables B-1 and C-1 derive from the three bases below, which say which
/// sublayer receives them; the dispatcher routes by base type. The concrete records are added by
/// the items that first send them (P3-1 onwards), with the fields 4.1 lists, and carry control
/// information explicitly, never derived from U_PDU contents (B-0014, B-0017, C-0036, C-0038).
/// </summary>
internal abstract record InternalPrimitive;

/// <summary>A primitive delivered to the SIS (CAS to SIS: C_ primitives of Table B-1).</summary>
internal abstract record ToSis : InternalPrimitive;

/// <summary>A primitive delivered to the CAS (SIS to CAS, B-0010; DTS to CAS, C-0026, C-0028, C-0030 to C-0032, C-0035).</summary>
internal abstract record ToCas : InternalPrimitive;

/// <summary>A primitive delivered to the DTS (CAS to DTS: C-0025, C-0027, C-0029, C-0033, C-0034).</summary>
internal abstract record ToDts : InternalPrimitive;
