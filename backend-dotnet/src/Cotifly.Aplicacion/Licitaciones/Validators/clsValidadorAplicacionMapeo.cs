using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.Validators;

/// <summary>
/// HU-044.3 (#254): validación local de <c>POST /web/licitaciones/aplicar-mapeo</c> ANTES de llamar a NEXO, con los códigos de NEXO. Solo
/// revisa la FORMA de la petición (partes que faltan, vienen mal formadas o se repiten); qué campos y transformaciones existen y si el
/// mapeo trae origen y destino lo decide NEXO (<c>CAMPO_INVALIDO</c>, <c>TRANSFORMACION_INVALIDA</c>, <c>CAMPO_REQUERIDO</c>), cuyo 400
/// llega con su código.
/// </summary>
public static class clsValidadorAplicacionMapeo
{
    /// <summary>Máximo de filas por petición: el tope de NEXO (el comentario del 2026-11-10 fijó 5000, no 1000).</summary>
    public const int MaximoFilas = 5000;

    /// <summary>Máximo de columnas del mapeo y de celdas por fila (el de NEXO).</summary>
    public const int MaximoColumnas = 100;

    /// <summary>Largo máximo del nombre de un campo o de una transformación (ninguno de NEXO se le acerca).</summary>
    public const int LargoMaximoNombre = 50;

    /// <summary>La última fila de una hoja de Excel.</summary>
    public const int UltimaFilaExcel = 1_048_576;

    /// <summary><c>primera_fila</c> por defecto (encabezado en la fila 1), la misma de NEXO.</summary>
    public const int PrimeraFilaPorDefecto = 2;

    /// <summary>Campo que no se mapea (NEXO lo acepta repetido).</summary>
    public const string CampoIgnorar = "ignorar";

    /// <summary>
    /// La entrada lista para NEXO (sin nulos: una celda <c>null</c> es una celda vacía) o el primer error: <c>CLIENTE_NIT_INVALIDO</c>,
    /// <c>MAPEO_REQUERIDO</c>, <c>DEMASIADAS_COLUMNAS</c>, <c>COLUMNA_INVALIDA</c> (columna nula o índice repetido),
    /// <c>COLUMNA_FUERA_DE_RANGO</c> (índice fuera de 0..99), <c>CAMPO_INVALIDO</c> (campo vacío o mal formado),
    /// <c>CAMPO_DUPLICADO</c> (dos columnas al mismo campo, salvo <c>ignorar</c>), <c>TRANSFORMACION_INVALIDA</c> (mal formada),
    /// <c>EXCEL_SIN_DATOS</c> (sin filas), <c>DEMASIADAS_FILAS</c> (más de <see cref="MaximoFilas"/>), <c>FILA_INVALIDA</c> (una fila
    /// <c>null</c> o una <c>primera_fila</c> fuera de 2..1.048.576) o <c>DEMASIADAS_COLUMNAS</c> (una fila con más de 100 celdas). Las
    /// posiciones del mensaje son base 1.
    /// </summary>
    public static clsResultado<clsPeticionAplicarMapeo> Validar(clsEntradaAplicacionMapeo entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (!clsValidadorNormalizacionValores.NitValido(entrada.NitCliente))
        {
            return new clsErrorDominio(clsCodigosError.ClienteNitInvalido, clsValidadorNormalizacionValores.MensajeNitInvalido);
        }

        var columnas = ValidarColumnas(entrada.Columnas);
        if (columnas.EsFallo)
        {
            return columnas.Error;
        }

        var filas = ValidarFilas(entrada.Filas);
        if (filas.EsFallo)
        {
            return filas.Error;
        }

        if (entrada.PrimeraFila is { } primera && primera is < PrimeraFilaPorDefecto or > UltimaFilaExcel)
        {
            return new clsErrorDominio(clsCodigosLineaSolicitud.FilaInvalida,
                $"'primera_fila' debe estar entre {PrimeraFilaPorDefecto} y {UltimaFilaExcel}: es la fila del encabezado más 1.");
        }

        if (entrada.PrimeraFila is { } inicio && inicio + filas.Valor.Count - 1 > UltimaFilaExcel)
        {
            return new clsErrorDominio(clsCodigosLineaSolicitud.FilaInvalida,
                $"Con 'primera_fila' = {inicio} las {filas.Valor.Count} filas pasan de la última fila de Excel ({UltimaFilaExcel}).");
        }

        return new clsPeticionAplicarMapeo(
            string.IsNullOrWhiteSpace(entrada.NitCliente) ? null : entrada.NitCliente.Trim(), columnas.Valor, filas.Valor, entrada.PrimeraFila);
    }

    private static clsResultado<IReadOnlyList<clsColumnaConfirmada>> ValidarColumnas(IReadOnlyList<clsColumnaEntradaMapeo?>? columnas)
    {
        if (columnas is null || columnas.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.MapeoRequerido, "Envía el mapeo confirmado: al menos una columna con su campo.");
        }

        if (columnas.Count > MaximoColumnas)
        {
            return new clsErrorDominio(clsCodigosError.DemasiadasColumnas,
                $"El mapeo admite hasta {MaximoColumnas} columnas; llegaron {columnas.Count}.");
        }

        var confirmadas = new List<clsColumnaConfirmada>(columnas.Count);
        var indices = new HashSet<int>();
        var campos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columnas.Count; i++)
        {
            var posicion = i + 1;
            var columna = columnas[i];
            if (columna?.Indice is not { } indice)
            {
                return new clsErrorDominio(clsCodigosError.ColumnaInvalida, $"La columna {posicion} del mapeo no trae su 'indice'.");
            }

            if (indice is < 0 or >= MaximoColumnas)
            {
                return new clsErrorDominio(clsCodigosError.ColumnaFueraDeRango,
                    $"La columna {posicion} del mapeo tiene el índice {indice}: debe estar entre 0 y {MaximoColumnas - 1}.");
            }

            if (!indices.Add(indice))
            {
                return new clsErrorDominio(clsCodigosError.ColumnaInvalida, $"La columna {posicion} del mapeo repite el índice {indice}.");
            }

            var campo = columna.Campo?.Trim();
            if (!NombreBienFormado(campo, permitirGuionBajo: false))
            {
                return new clsErrorDominio(clsCodigosError.CampoInvalido, string.IsNullOrEmpty(campo)
                    ? $"La columna {posicion} del mapeo no trae su 'campo' (usa '{CampoIgnorar}' para no mapearla)."
                    : $"La columna {posicion} del mapeo tiene un campo mal formado.");
            }

            if (!string.Equals(campo, CampoIgnorar, StringComparison.OrdinalIgnoreCase) && campos.TryGetValue(campo!, out var anterior))
            {
                return new clsErrorDominio(clsCodigosError.CampoDuplicado,
                    $"Las columnas {anterior} y {posicion} del mapeo apuntan al mismo campo ('{campo}').");
            }

            campos.TryAdd(campo!, posicion);
            var transformacion = string.IsNullOrWhiteSpace(columna.Transformacion) ? null : columna.Transformacion.Trim();
            if (transformacion is not null && !NombreBienFormado(transformacion, permitirGuionBajo: true))
            {
                return new clsErrorDominio(clsCodigosError.TransformacionInvalida,
                    $"La columna {posicion} del mapeo tiene una transformación mal formada.");
            }

            confirmadas.Add(new clsColumnaConfirmada(indice, campo!, transformacion));
        }

        return confirmadas;
    }

    private static clsResultado<IReadOnlyList<IReadOnlyList<string>>> ValidarFilas(IReadOnlyList<IReadOnlyList<string?>?>? filas)
    {
        if (filas is null || filas.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.ExcelSinDatos, "Envía las filas de la hoja (sin el encabezado).");
        }

        if (filas.Count > MaximoFilas)
        {
            return new clsErrorDominio(clsCodigosError.DemasiadasFilas,
                $"Se pueden importar hasta {MaximoFilas} filas por licitación; llegaron {filas.Count}.");
        }

        var limpias = new List<IReadOnlyList<string>>(filas.Count);
        for (var i = 0; i < filas.Count; i++)
        {
            if (filas[i] is not { } fila)
            {
                return new clsErrorDominio(clsCodigosLineaSolicitud.FilaInvalida, $"La fila {i + 1} de 'filas' es nula: envía una lista de celdas (vacía si no tiene datos).");
            }

            if (fila.Count > MaximoColumnas)
            {
                return new clsErrorDominio(clsCodigosError.DemasiadasColumnas,
                    $"La fila {i + 1} de 'filas' tiene {fila.Count} celdas: el máximo es {MaximoColumnas}.");
            }

            limpias.Add([.. fila.Select(c => c ?? string.Empty)]);
        }

        return limpias;
    }

    /// <summary>
    /// ¿Un nombre de campo de NEXO (letras y dígitos, empieza por letra: <c>tipoVehiculo</c>) o de transformación (además guion bajo:
    /// <c>peso_toneladas_a_kg</c>) bien formado, de hasta <see cref="LargoMaximoNombre"/> caracteres?
    /// </summary>
    private static bool NombreBienFormado(string? nombre, bool permitirGuionBajo) =>
        !string.IsNullOrEmpty(nombre) && nombre.Length <= LargoMaximoNombre && char.IsAsciiLetter(nombre[0])
        && nombre.All(c => char.IsAsciiLetterOrDigit(c) || (permitirGuionBajo && c == '_'));
}
