using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Implementación de <see cref="IClsVistaPreviaCorreoService"/>: mismo asunto, cuerpo, resumen y adjunto que usaría
/// <see cref="clsCorreoService"/>, sin tocar el transporte ni la base. HU-033 (#199): trae el cuerpo en HTML con el resumen (precio por
/// ruta y total, sin desglose), la copia oculta al comercial y, si el ambiente redirige los correos, el aviso "ambiente de pruebas"
/// (entonces sin copia oculta: el correo redirigido sale sin CC ni CCO, OBS-1027-5 de #226).
/// </summary>
public sealed class clsVistaPreviaCorreoService(
    IClsCotizacionRepository cotizaciones,
    IClsDestinatariosPreviosService destinatariosPrevios,
    IClsUsuarioWebRepository usuarios,
    IClsCalendarioNegocio calendario,
    IOptions<clsOpcionesCorreo> opciones) : IClsVistaPreviaCorreoService
{
    public async Task<clsResultado<clsVistaPreviaCorreo>> PrevisualizarAsync(
        Guid usuarioId, string folio, clsBorradorCorreo? borrador = null, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio))
        {
            return NoEncontrada();
        }

        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        // DEF-1027B-2 y tope (#228): la misma validación que el envío, sobre lo que trae el borrador (sin para, se precargan los previos).
        if (borrador is not null
            && clsCorreoService.ValidarBorrador(
                borrador.Para, borrador.Cc, borrador.Asunto, borrador.Cuerpo, exigePara: false, opciones.Value.MaximoDestinatarios) is { } invalido)
        {
            return invalido;
        }

        var para = Limpiar(borrador?.Para);
        var cc = Limpiar(borrador?.Cc);
        if (para.Count == 0)
        {
            var previos = await destinatariosPrevios.ObtenerAsync(usuarioId, cotizacion.NitCliente, cancellationToken);
            para = [.. previos.Para];
            if (cc.Count == 0)
            {
                cc = [.. previos.Cc];
            }
        }

        var cuerpo = string.IsNullOrWhiteSpace(borrador?.Cuerpo) ? clsPlantillaCorreo.CuerpoPorDefecto : borrador.Cuerpo.Trim();
        var asunto = string.IsNullOrWhiteSpace(borrador?.Asunto) ? clsPlantillaCorreo.AsuntoPorDefecto(cotizacion.Folio) : borrador.Asunto.Trim();
        var resumen = clsPlantillaCorreo.Resumen(cotizacion);
        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        var proteccion = opciones.Value.Proteccion;
        // HU-033: se redirige salvo con las dos llaves del envío real; sin un buzón válido el envío no sale (la vista previa lo avisa).
        var redirige = proteccion.Redirige;
        var buzon = redirige ? clsProteccionCorreo.BuzonValido(proteccion) : null;
        return new clsVistaPreviaCorreo(asunto, cuerpo, para, cc, clsCorreoService.NombreAdjunto(cotizacion))
        {
            CuerpoHtml = clsPlantillaCorreo.Html(
                cuerpo,
                resumen,
                firmaComercial: clsPlantillaCorreo.FirmaComercial(usuario?.NombreCompleto, usuario?.Email, usuario?.Telefono),
                // #262: los mismos datos de la ficha que el envío.
                datos: clsPlantillaCorreo.Datos(cotizacion, calendario)),
            Resumen = resumen,
            // OBS-1027-5 (#226, ADR-023): al redirigir el correo sale solo al buzón de pruebas, sin CC ni CCO (clsProteccionCorreo): la
            // vista previa no anuncia una copia oculta que no se envía.
            CopiaOculta = redirige ? null : clsCorreoService.CopiaOculta(usuario?.Email, para, cc),
            EntornoPruebas = redirige,
            RedirigidoA = buzon,
            Aviso = !redirige ? null : buzon is null ? clsProteccionCorreo.MensajeSinBuzon : clsProteccionCorreo.AvisoVistaPrevia(buzon),
        };
    }

    private static List<string> Limpiar(IReadOnlyList<string>? emails) =>
        emails is null ? [] : [.. emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())];

    private static clsErrorDominio NoEncontrada() =>
        new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
