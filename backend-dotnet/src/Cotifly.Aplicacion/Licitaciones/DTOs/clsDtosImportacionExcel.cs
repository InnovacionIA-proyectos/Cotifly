using System.Text.Json;

namespace Cotifly.Aplicacion.Licitaciones.DTOs;

// HU-044.1 (#252, ADR-028): lo que Cotifly pide y recibe de la importación inteligente de NEXO (CONTRATO.md §3.3 y §3.19.1 de NEXO).
// Son los datos de Aplicación, no la forma del cable: el JSON camelCase de NEXO lo arma y lo lee el adaptador con sus DTOs privados.
// El cliente viaja siempre como el NIT de la licitación (NEXO lo normaliza y lo busca); Cotifly no guarda el clienteId de NEXO.

/// <summary>
/// <c>POST sugerir-mapeo</c>: los encabezados de UNA hoja (1 a 100, en el orden del archivo) y sus filas (NEXO usa las 20 primeras;
/// máx. 1000). <paramref name="ClienteNit"/> es el NIT de la licitación (opcional: sin él NEXO usa solo el diccionario general).
/// </summary>
public sealed record clsPeticionSugerirMapeo(
    string? ClienteNit, IReadOnlyList<string> Encabezados, IReadOnlyList<IReadOnlyList<string>>? Filas);

/// <summary>
/// Propuesta de mapeo de NEXO. <paramref name="MotivoSinIa"/>: <c>PLANTILLA_RECONOCIDA</c>, <c>DICCIONARIO_COMPLETO</c>,
/// <c>IA_NO_DISPONIBLE</c> o un <c>IA_*</c> del proveedor; <c>null</c> si la IA participó. <paramref name="PlantillaId"/>: la plantilla del
/// cliente que se reconoció.
/// </summary>
public sealed record clsSugerenciaMapeo(
    bool IaUsada, string? MotivoSinIa, IReadOnlyList<clsColumnaMapeada> Columnas, IReadOnlyList<string> Dudas, Guid? PlantillaId);

/// <summary>
/// Una columna de la propuesta: <paramref name="Indice"/> base 0, <paramref name="Campo"/> de NEXO (o <c>ignorar</c>),
/// <paramref name="Origen"/> = <c>plantilla</c> | <c>diccionario</c> | <c>ia</c> | <c>ninguno</c> y <paramref name="Confianza"/> de 0 a 1.
/// </summary>
public sealed record clsColumnaMapeada(
    int Indice, string Columna, string Campo, string Transformacion, double Confianza, string Origen, string? Nota);

/// <summary>Una columna confirmada por el usuario: índice base 0, campo de NEXO (o <c>ignorar</c>) y transformación (vacía = <c>ninguna</c>).</summary>
public sealed record clsColumnaConfirmada(int Indice, string Campo, string? Transformacion);

/// <summary>
/// <c>POST aplicar-mapeo</c>: el mapeo confirmado (1 a 100 columnas), las filas SIN el encabezado (máx. 5000) y el número de Excel de la
/// primera fila enviada (<c>null</c> = 2, el valor por defecto de NEXO).
/// </summary>
public sealed record clsPeticionAplicarMapeo(
    string? ClienteNit, IReadOnlyList<clsColumnaConfirmada> Columnas, IReadOnlyList<IReadOnlyList<string>> Filas, int? PrimeraFila);

/// <summary>Resultado de <c>aplicar-mapeo</c>: las líneas válidas, en el orden del archivo, y los errores por fila (máx. 100 en la lista).</summary>
public sealed record clsResultadoAplicarMapeo(
    int TotalFilas, int FilasVacias, int FilasConErrores, IReadOnlyList<clsLineaImportada> Lineas, IReadOnlyList<clsErrorDeFila> Errores,
    int TotalErrores);

/// <summary>
/// Una fila convertida por NEXO, con la forma de una línea de su captura más la fila de Excel, la referencia y la fecha límite (datos de la
/// solicitud, no de la línea: decide quien la consume).
/// </summary>
public sealed record clsLineaImportada(
    int Fila,
    string Origen,
    string Destino,
    string? TipoVehiculo,
    string? Carroceria,
    string? Tipologia,
    string? LineaNegocio,
    string? ClaseViaje,
    string? ClaseContenedor,
    bool? Exclusivo,
    bool? Consolidado,
    bool? RetornaContenedor,
    string? CiudadDevolucionContenedor,
    bool? LevanteContenedorDiferente,
    string? CiudadLevanteContenedor,
    decimal? HorasCargue,
    decimal? HorasDescargue,
    decimal? Peso,
    decimal? ValorMercancia,
    IReadOnlyList<clsPosicionImportada>? Posiciones,
    string? Referencia,
    DateTimeOffset? Deadline);

/// <summary>Una posición de carga de una línea importada (medidas en cm, peso por unidad en kg).</summary>
public sealed record clsPosicionImportada(
    string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg, string? CodigoTipoMercancia);

/// <summary>Un error de una fila de <c>aplicar-mapeo</c>: la fila de Excel, el campo de NEXO (si es de uno solo), el código y el mensaje de NEXO.</summary>
public sealed record clsErrorDeFila(int Fila, string? Campo, string Codigo, string Mensaje);

/// <summary>Un texto libre del Excel para <c>normalizar-valores</c>: <paramref name="Campo"/> = <c>tipoVehiculo</c> | <c>carroceria</c> | <c>lineaNegocio</c> | <c>ciudad</c>.</summary>
public sealed record clsValorANormalizar(string Campo, string? Valor);

/// <summary>
/// Resultado de <c>normalizar-valores</c>. <paramref name="MotivoSinIa"/>: <c>SIN_PENDIENTES</c>, <c>IA_NO_DISPONIBLE</c> o un <c>IA_*</c>;
/// <c>null</c> si la IA participó.
/// </summary>
public sealed record clsResultadoNormalizacion(bool IaUsada, string? MotivoSinIa, int TotalDistintos, IReadOnlyList<clsValorNormalizado> Valores);

/// <summary>
/// Un texto distinto ya normalizado. <paramref name="Estado"/> = <c>resuelto</c> | <c>ambiguo</c> | <c>sin_resolver</c>; un resultado con
/// <paramref name="Origen"/> = <c>ia</c> es una propuesta. <paramref name="Candidatos"/> solo en una ciudad ambigua (máx. 8).
/// </summary>
public sealed record clsValorNormalizado(
    string Campo, string Valor, string Estado, string? Codigo, string? Descripcion, int? MunicipioId, string Origen, double Confianza,
    string? Nota, IReadOnlyList<clsCandidatoCiudad>? Candidatos);

/// <summary>Un municipio candidato de una ciudad ambigua.</summary>
public sealed record clsCandidatoCiudad(int MunicipioId, string CodigoDane, string Municipio, string Departamento);

/// <summary>
/// <c>PUT plantillas-mapeo</c>: la plantilla del cliente (NIT obligatorio: si no tiene cliente local en NEXO, NEXO lo crea desde el DW) para
/// esos encabezados (1 a 100, en orden) con el mapeo confirmado.
/// </summary>
public sealed record clsPeticionPlantillaMapeo(
    string ClienteNit, string? Nombre, IReadOnlyList<string> Encabezados, IReadOnlyList<clsColumnaConfirmada> Columnas);

/// <summary>Una plantilla de mapeo guardada en NEXO (<paramref name="ClienteId"/> es el id local de NEXO: Cotifly no lo guarda).</summary>
public sealed record clsPlantillaMapeo(
    Guid Id, Guid ClienteId, string? ClienteNombre, string? Nombre, IReadOnlyList<string> Encabezados,
    IReadOnlyList<clsColumnaConfirmada> Columnas, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Resultado de guardar: la plantilla y si se creó (<c>201</c> de NEXO) o reemplazó a la que tenía esos encabezados (<c>200</c>).</summary>
public sealed record clsPlantillaGuardada(clsPlantillaMapeo Plantilla, bool Creada);

/// <summary>
/// Filtros de <c>GET diccionario-cliente</c>: <paramref name="Tipo"/> = <c>columna</c> | <c>unidad</c> | <c>valor</c>, <paramref name="Q"/>
/// busca en el término y el concepto, <paramref name="SoloActivos"/> omite los inactivos e <paramref name="IncluirGenerales"/> (por defecto
/// sí) los que aplican a todos los clientes.
/// </summary>
public sealed record clsFiltroDiccionarioCliente(string? Tipo = null, string? Q = null, bool SoloActivos = false, bool IncluirGenerales = true);

/// <summary>
/// Un término del diccionario de NEXO (<paramref name="ClienteId"/> <c>null</c> = aplica a todos los clientes). <paramref name="ClienteIdLeido"/>
/// dice si NEXO mandó <c>clienteId</c> de verdad (<c>null</c> o un GUID); si no vino (un cambio de forma de NEXO), el <c>null</c> de
/// <paramref name="ClienteId"/> no significa "general" (#256). Por defecto <c>false</c>: falla cerrado.
/// </summary>
public sealed record clsTerminoCliente(
    Guid Id, Guid? ClienteId, string? ClienteNombre, string Termino, string Tipo, string Concepto, string? Contexto, string? Ejemplo,
    bool Activo, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, bool ClienteIdLeido = false)
{
    /// <summary>
    /// ¿Aplica a todos los clientes? Solo si NEXO mandó <c>clienteId = null</c> explícito y sin nombre de cliente. Ante la duda, no: así un
    /// cambio de forma de NEXO nunca muestra los términos de otro cliente como generales.
    /// </summary>
    public bool EsGeneral => ClienteIdLeido && ClienteId is null && string.IsNullOrWhiteSpace(ClienteNombre);
}

/// <summary>
/// Los campos editables de un término del diccionario del cliente, tal como viajan a NEXO al crearlo o editarlo. El cliente va aparte: al
/// crear, por NIT; al editar no cambia.
/// </summary>
public sealed record clsDatosTerminoCliente(string Termino, string Tipo, string Concepto, string? Contexto, string? Ejemplo, bool Activo);

/// <summary>Los valores que acepta el diccionario de NEXO (tipos, campos de NEXO, unidades y contextos), para armar los formularios.</summary>
public sealed record clsOpcionesDiccionarioCliente(
    IReadOnlyList<string> Tipos, IReadOnlyList<string> CamposDestino, IReadOnlyList<string> Unidades, IReadOnlyList<string> Contextos);

/// <summary>
/// El Excel que viaja a NEXO (<c>multipart</c>, campo <c>archivo</c>) con su nombre original. El adaptador no lo abre ni lo valida: el endpoint
/// de Cotifly que lo recibe (#253 lectura, #256 recetas) solo comprueba el tamaño (≤ 10 MB) y la extensión (<c>.xlsx</c>/<c>.xlsm</c>), sin abrir
/// el libro, y el resto lo valida NEXO, cuyo rechazo llega con su código.
/// </summary>
public sealed record clsArchivoImportacion(string NombreArchivo, byte[] Contenido);

/// <summary>
/// <c>POST leer-estructura</c>. <paramref name="Instrucciones"/> (máx. 4000) siempre consulta a la IA; <paramref name="RecetaJson"/> es la
/// <c>receta</c> de una lectura anterior como TEXTO JSON OPACO (sola se ejecuta sin IA; con instrucciones, la IA la ajusta). Sin NIT NEXO no
/// busca formato guardado.
/// </summary>
public sealed record clsPeticionLeerEstructura(
    clsArchivoImportacion Archivo, string? ClienteNit, string? Instrucciones, string? RecetaJson);

/// <summary>
/// Lectura de NEXO. <paramref name="Receta"/> es la receta que se ejecutó, tal cual (JSON opaco: Cotifly no la tipa ni la cambia; se reenvía
/// para corregirla o guardarla). <paramref name="Tablas"/> tiene la forma de las hojas de <c>analizar-excel</c>: cada una entra a
/// <c>sugerir-mapeo</c> sin transformarla. <paramref name="OrigenReceta"/> = <c>guardada</c> | <c>ia</c> | <c>manual</c> | <c>automatica</c>.
/// </summary>
public sealed record clsLecturaEstructura(
    string Archivo,
    string OrigenReceta,
    double? Similitud,
    Guid? RecetaGuardadaId,
    bool IaUsada,
    string? MotivoSinIa,
    JsonElement Receta,
    string? Resumen,
    IReadOnlyList<string> Dudas,
    IReadOnlyList<clsHojaDelArchivo> HojasDelArchivo,
    IReadOnlyList<clsHojaExcel> Tablas,
    IReadOnlyList<string> Avisos,
    clsCambiosDeFormato? Cambios);

/// <summary>Una hoja del libro, con su tamaño y si la receta la usó.</summary>
public sealed record clsHojaDelArchivo(string Nombre, bool Oculta, int Filas, int Columnas, bool Usada);

/// <summary>Títulos que ya no aparecen y títulos nuevos respecto al formato guardado del cliente.</summary>
public sealed record clsCambiosDeFormato(IReadOnlyList<string> Faltan, IReadOnlyList<string> Nuevos);

/// <summary>
/// <c>PUT recetas-lectura</c>: el MISMO Excel con el que se confirmó la receta, la receta (texto JSON opaco, máx. 60.000 caracteres), el NIT
/// (obligatorio: si no tiene cliente local, NEXO lo crea desde el DW), un nombre opcional (máx. 100) y las instrucciones que dio la persona
/// (máx. 4000).
/// </summary>
public sealed record clsPeticionGuardarReceta(
    clsArchivoImportacion Archivo, string RecetaJson, string ClienteNit, string? Nombre, string? Instrucciones);

/// <summary>Un formato de Excel guardado en NEXO para un cliente. <paramref name="Receta"/> viaja opaca, tal como la devuelve NEXO.</summary>
public sealed record clsRecetaGuardada(
    Guid Id, Guid ClienteId, string? ClienteNombre, string? Nombre, JsonElement Receta, string? Instrucciones, int Usos,
    DateTimeOffset? UltimoUsoAt, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Resultado de guardar una receta: el formato y si es nuevo (<c>201</c>) o reemplazó a uno parecido del cliente (<c>200</c>).</summary>
public sealed record clsRecetaGuardadaResultado(clsRecetaGuardada Receta, bool Creada);
