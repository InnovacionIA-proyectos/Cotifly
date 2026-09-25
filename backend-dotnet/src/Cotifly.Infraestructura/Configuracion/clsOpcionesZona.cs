using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Zona horaria de negocio (sección <c>Zona</c>, variable <c>Zona__ZonaHoraria</c>).
/// Se usa para fechas visibles como el <c>DDMMAAAA</c> del folio (ADR-011). El Python usaba UTC:
/// desviación deliberada del plan de migración.
/// </summary>
public sealed class clsOpcionesZona
{
    public const string Seccion = "Zona";

    /// <summary>Identificador IANA de la zona. Por defecto <c>America/Bogota</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ZonaHoraria { get; set; } = "America/Bogota";
}
