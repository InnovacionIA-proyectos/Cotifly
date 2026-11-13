namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Lo que viajó a Nexo en el alta de una solicitud: si las líneas de los camiones del cubicador llevaron sus
/// códigos de vehículo y carrocería (<c>Nexo:EnviarCodigosCubicador</c> al momento del envío). El emparejamiento del polling compara contra
/// esto y no contra la bandera actual: si la bandera cambia entre el envío y la lectura, comparar con la actual cruzaría costos entre rutas.
/// Se guarda en <c>EventosHerramienta</c> (sin columna nueva), en la MISMA transacción que el <c>solicitudNexoId</c>. Sin registro
/// (solicitudes anteriores, o un guardado que falló) se asume que los códigos NO viajaron: es el caso seguro (no se compara el vehículo).
/// </summary>
public sealed record clsEnvioNexoRegistrado(string Folio, string SolicitudNexoId, bool CodigosCubicadorEnviados)
{
    /// <summary>Herramienta con que se guarda en <c>EventosHerramienta</c>.</summary>
    public const string Herramienta = "envio_nexo_registrado";

    /// <summary>
    /// Los códigos de vehículo que viajaron traducidos por el diccionario de alias de Nexo (código de Cotifly → código
    /// enviado). El polling compara el vehículo de la línea de Nexo contra el código enviado: sin esto, el emparejamiento no reconocería la
    /// línea y la regla "la Mesa manda" tomaría la traducción como un cambio de la Mesa. Vacío si ninguno (o sin registro).
    /// </summary>
    public IReadOnlyDictionary<string, string> EquivalenciasVehiculo { get; init; } = new Dictionary<string, string>();
}
