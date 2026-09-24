using System.Collections.Concurrent;
using System.Globalization;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Fuente de margen en memoria con las filas de <c>tests/fixtures/sap/rentab_cliente_muestra.json</c> del Python
/// (mismos SalesOrgId, TrqType, OrderPartyId y Value). Registra las consultas y puede simular SAP caído.
/// Los adaptadores reales (<c>export_json</c> | <c>odata</c>, #13) se prueban con el archivo de la muestra y con <c>clsSapTmFalso</c>.
/// </summary>
public sealed class clsFuenteMargenMuestra : IClsFuenteMargen
{
    /// <summary>Filas de la muestra: (SalesOrgId, TrqType, OrderPartyId, Value).</summary>
    public static readonly IReadOnlyList<(string SalesOrg, string TrqType, string Bp, string Valor)> Filas =
    [
        ("50000005", "OPTN", "15927", "13.5"),
        ("50000005", "OPIM", "580", "5"),
        ("50000005", "OPEX", "704", "8.2"),
        ("50000005", "OPTI", "1123", "12"),
        ("50000005", "OPDS", "2044", "9.75"),
        ("50000010", "OHUB", "3301", "15"),
        ("50000010", "OH3P", "3302", "18.4"),
        ("50000005", "OPSC", "4110", "7"),
        ("50000005", "OPTN", "5000", "-19.2"),
        ("50000005", "OPIM", "5001", "99.9"),
    ];

    private readonly ConcurrentQueue<(string SalesOrg, string TrqType, string Bp)> _consultas = new();

    public string Modo => "muestra";

    /// <summary>Si es false, se comporta como la fuente no configurada (<c>SapTm:Modo</c> vacío): no se consulta y no tiene registros.</summary>
    public bool EstaConfigurada { get; set; } = true;

    /// <summary>Si es true, toda consulta falla como si SAP TM estuviera caído.</summary>
    public bool NoDisponible { get; set; }

    /// <summary>Consultas recibidas, en orden.</summary>
    public IReadOnlyList<(string SalesOrg, string TrqType, string Bp)> Consultas => [.. _consultas];

    public Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default)
    {
        _consultas.Enqueue((salesOrg, trqType, bpCliente));
        if (!EstaConfigurada)
        {
            return Task.FromResult<decimal?>(null);
        }

        if (NoDisponible)
        {
            throw new clsFuenteMargenNoDisponibleException("SAP TM no respondió (timeout simulado).");
        }

        foreach (var fila in Filas)
        {
            if (fila.SalesOrg == salesOrg && fila.TrqType == trqType && fila.Bp == bpCliente)
            {
                return Task.FromResult<decimal?>(decimal.Parse(fila.Valor, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
        }

        return Task.FromResult<decimal?>(null);
    }
}
