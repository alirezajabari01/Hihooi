namespace Hihooi.Core.Model;

/// <summary>Affecting class of a statement/transaction for a given table (paper §4.1).</summary>
public enum AffectingClass
{
    /// <summary>Row Affecting Statement — specific PK/unique rows.</summary>
    Ras = 0,

    /// <summary>Column Affecting Statement — some columns.</summary>
    Cas = 1,

    /// <summary>Table Affecting Statement — all columns / whole table (incl. DDL).</summary>
    Tas = 2
}
