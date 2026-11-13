using System.Collections.Concurrent;
using System.Globalization;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Fuente de margen en memoria con las filas de la muestra de <c>RentabClienteSet</c> del Python (mismos SalesOrgId, TrqType,
/// OrderPartyId y Value). Registra las consultas y puede simular la fuente caída o nunca sincronizada. La fuente real (la copia local,
/// <c>clsFuenteMargenTablaLocal</c>) se prueba contra SQL Server sembrando estas mismas filas en <c>SapMargenCliente</c>.
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

    /// <summary>Fecha de las filas en la copia local (la de <c>actualizadoAt</c>), en hora de Colombia.</summary>
    public static readonly DateTimeOffset ActualizadoMuestra = new(2026, 9, 23, 8, 0, 0, TimeSpan.FromHours(-5));

    private readonly ConcurrentQueue<(string SalesOrg, string TrqType, string Bp)> _consultas = new();

    public string Modo => "muestra";

    /// <summary>
    /// Si es false, se comporta como una copia local que nunca se sincronizó: responde "no configurada" y no registra la
    /// consulta (la fuente real tampoco lee la tabla en ese caso).
    /// </summary>
    public bool EstaConfigurada { get; set; } = true;

    /// <summary>Si es true, toda consulta falla como si la fuente no se pudiera leer.</summary>
    public bool NoDisponible { get; set; }

    /// <summary>
    /// Fin de la última sincronización exitosa que informa la fuente. Por defecto, 2 h antes del reloj falso de las pruebas
    /// (<c>clsRelojFalso</c>): una copia al día.
    /// </summary>
    public DateTimeOffset? UltimaSincronizacion { get; set; } = new DateTimeOffset(2026, 9, 24, 6, 0, 0, TimeSpan.FromHours(-5));

    /// <summary>Consultas recibidas, en orden.</summary>
    public IReadOnlyList<(string SalesOrg, string TrqType, string Bp)> Consultas => [.. _consultas];

    public Task<clsConsultaMargenSap> ConsultarAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default)
    {
        if (!EstaConfigurada)
        {
            return Task.FromResult(clsConsultaMargenSap.NoConfigurada);
        }

        _consultas.Enqueue((salesOrg, trqType, bpCliente));
        if (NoDisponible)
        {
            throw new clsFuenteMargenNoDisponibleException("SAP TM no respondió (timeout simulado).");
        }

        foreach (var fila in Filas)
        {
            if (fila.SalesOrg == salesOrg && fila.TrqType == trqType && fila.Bp == bpCliente)
            {
                return Task.FromResult(clsConsultaMargenSap.ConValor(
                    decimal.Parse(fila.Valor, NumberStyles.Float, CultureInfo.InvariantCulture), ActualizadoMuestra, UltimaSincronizacion));
            }
        }

        return Task.FromResult(clsConsultaMargenSap.SinRegistro(UltimaSincronizacion));
    }
}
