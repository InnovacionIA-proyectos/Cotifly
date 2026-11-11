using System.Text.Json.Serialization;

namespace Cotifly.Aplicacion.Redaccion.DTOs;

/// <summary>
/// Lo ÚNICO que recibe el redactor de la IA (HU-013.5, ADR-018 §2a): nombres y textos de presentación. Por diseño no tiene ningún
/// campo numérico: nunca lleva costos, margen, precios, totales ni la TRM (con el margen y un precio, el modelo podría deducir el
/// costo de Nexo). Tampoco lleva el folio, el NIT ni el nombre o el correo del comercial (minimización: el prompt prohíbe firmar). El
/// nombre del cliente sí va al proveedor de IA (ADR-018 §2a, Ley 1581). Cada texto llega recortado a <see cref="LargoMaximoCampo"/>.
/// </summary>
/// <param name="Cliente">Nombre del cliente (<c>Cotizaciones.clienteNombre</c>).</param>
/// <param name="LineaNegocio">Línea de negocio (ej. <c>Nacional</c>).</param>
/// <param name="Rutas">Origen y destino de cada ruta (una en una cotización spot; las del pliego, hasta <see cref="MaximoRutas"/>).</param>
/// <param name="TipoVehiculo">Tipo de vehículo (o los distintos del pliego, separados por coma).</param>
/// <param name="Conexos">Nombres de los servicios conexos incluidos (sin cantidades ni valores).</param>
/// <param name="Moneda">Moneda de la cotización (<c>COP</c> o <c>USD</c>).</param>
public sealed record clsContextoRedaccion(
    string? Cliente,
    string? LineaNegocio,
    IReadOnlyList<clsRutaRedaccion> Rutas,
    string? TipoVehiculo,
    IReadOnlyList<string> Conexos,
    string Moneda)
{
    /// <summary>Caracteres máximos de cada texto del contexto (cliente, línea, ciudades, vehículo y cada conexo).</summary>
    public const int LargoMaximoCampo = 150;

    /// <summary>Rutas que se envían al modelo como máximo (un pliego puede tener hasta 1000).</summary>
    public const int MaximoRutas = 30;

    /// <summary>Caracteres máximos de las indicaciones del comercial para el redactor (HU-013.18, #296).</summary>
    public const int LargoMaximoIndicaciones = 500;

    /// <summary>
    /// Indicaciones del comercial para orientar la redacción (HU-013.18, #296): una PREFERENCIA de enfoque o tono (ej. "destacar la
    /// experiencia en carga refrigerada, tono cercano"), nunca una instrucción que pase por encima de las reglas estrictas del prompt. Es
    /// opcional: cuando no viene (<c>null</c>) el contexto queda idéntico al de siempre, porque solo se serializa a <c>indicaciones_comercial</c>
    /// si trae texto (<see cref="JsonIgnoreCondition.WhenWritingNull"/>). No es la nota de la Mesa de Costeo ni se guarda en la cotización.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndicacionesComercial { get; init; }
}

/// <summary>Origen y destino de una ruta (solo los nombres de las ciudades).</summary>
public sealed record clsRutaRedaccion(string Origen, string Destino);

/// <summary>Respuesta cruda del redactor: el texto del modelo (sin validar) y el modelo que respondió, si el proveedor lo informa.</summary>
public sealed record clsRespuestaRedactor(string? Texto, string? Modelo);

/// <summary>Proveedor y modelo configurados del redactor y la versión (hash) de su prompt, para la auditoría.</summary>
public sealed record clsIdentidadRedactor(string Proveedor, string Modelo, string VersionPrompt);

/// <summary>
/// Asunto e introducción estándar de la cotización: exactamente los que imprime el PDF de su variante cuando no hay textos de la IA
/// ni del comercial (tarifa única o pliego). Son el respaldo del redactor.
/// </summary>
public sealed record clsTextosBase(string Asunto, string Introduccion);

/// <summary>
/// Resultado de la redacción (HU-013.5): los textos (de la IA validados, o los estándar si no) y lo que se audita: proveedor,
/// modelo, versión del prompt, duración y si se usó el respaldo con su motivo.
/// </summary>
public sealed record clsResultadoRedaccion(
    string Asunto,
    string Introduccion,
    string Fuente,
    string Proveedor,
    string Modelo,
    string VersionPrompt,
    int DuracionMs,
    bool Respaldo,
    string? MotivoRespaldo);
