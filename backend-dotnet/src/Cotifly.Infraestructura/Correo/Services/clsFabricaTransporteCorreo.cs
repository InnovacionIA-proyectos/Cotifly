using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Correo.Services;

/// <summary>
/// Elige el transporte por <c>Correo:Modo</c> (<c>construir_servicio_correo</c> del Python): <c>smtp_relay</c> →
/// <see cref="clsTransporteSmtpRelay"/>; <c>wscorreos</c> / <c>smtp</c> → <see cref="clsTransporteCorreoNoImplementado"/>.
/// </summary>
public static class clsFabricaTransporteCorreo
{
    public static IClsTransporteCorreo Crear(IServiceProvider proveedor)
    {
        ArgumentNullException.ThrowIfNull(proveedor);
        var modo = proveedor.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value.ModoNormalizado;
        return modo == clsOpcionesCorreoTransporte.ModoSmtpRelay
            ? proveedor.GetRequiredService<clsTransporteSmtpRelay>()
            : new clsTransporteCorreoNoImplementado(modo);
    }
}
