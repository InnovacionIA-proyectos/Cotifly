using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun;

/// <summary>
/// Adaptador de <see cref="IClsCalendarioNegocio"/>: <see cref="IClsReloj.AhoraUtc"/> convertido a <c>Zona:ZonaHoraria</c>
/// (validada al arrancar, <see cref="clsValidadorOpcionesZona"/>).
/// </summary>
public sealed class clsCalendarioNegocio(IClsReloj reloj, IOptions<clsOpcionesZona> opciones) : IClsCalendarioNegocio
{
    private readonly TimeZoneInfo _zona = TimeZoneInfo.FindSystemTimeZoneById(opciones.Value.ZonaHoraria);

    public DateOnly Hoy => FechaDe(reloj.AhoraUtc);

    public string ZonaHoraria { get; } = opciones.Value.ZonaHoraria;

    public DateOnly FechaDe(DateTimeOffset instante) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, _zona).DateTime);

    public DateTimeOffset InicioDelDia(DateOnly fecha)
    {
        var local = fecha.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _zona.GetUtcOffset(local));
    }
}
