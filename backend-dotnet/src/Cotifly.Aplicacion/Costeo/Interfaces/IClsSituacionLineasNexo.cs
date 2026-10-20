using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// #181: la última situación que Nexo informó de la línea de cada camión (<see cref="clsSituacionLineaNexo"/>: estado de la línea y motivo
/// por el que sigue pendiente), para mostrarla en <c>GET .../costeo</c>. Decisión del usuario: se EXPONE sin guardarla en la base. La
/// llena el polling con cada foto que aplica y guarda; la lee la vista del costeo. Un camión sin dato (proceso reiniciado, Nexo sin
/// #309 o la foto aún no leída) sale con <c>null</c>.
/// </summary>
public interface IClsSituacionLineasNexo
{
    /// <summary>
    /// Reemplaza la situación de los camiones de la cotización <paramref name="cotizacionId"/> con la de la última foto de Nexo (por id de
    /// camión). Un diccionario vacío borra lo anterior.
    /// </summary>
    void Registrar(Guid cotizacionId, IReadOnlyDictionary<Guid, clsSituacionLineaNexo> porCamion);

    /// <summary>La situación por id de camión de la última foto registrada, o <c>null</c> si no hay (o ya venció).</summary>
    IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? Obtener(Guid cotizacionId);
}
