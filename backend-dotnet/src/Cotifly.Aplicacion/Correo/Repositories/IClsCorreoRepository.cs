using Cotifly.Dominio.Correo;

namespace Cotifly.Aplicacion.Correo.Repositories;

/// <summary>Puerto de los correos enviados y sus destinatarios (<c>Correos</c>, <c>CorreoDestinatarios</c>).</summary>
public interface IClsCorreoRepository
{
    /// <summary>Ventana de deduplicación del contrato: 5 minutos.</summary>
    static readonly TimeSpan VentanaDuplicado = TimeSpan.FromMinutes(5);

    void Agregar(clsCorreo correo);

    /// <summary>
    /// ¿Ya hay un envío exitoso (<c>enviado</c>) de la cotización al mismo conjunto de destinatarios
    /// <c>para</c> (sin distinguir mayúsculas ni orden) dentro de la ventana? La ventana se mide con
    /// el reloj de la aplicación (<c>IClsReloj</c>), no con el de la base.
    /// </summary>
    Task<bool> ExisteDuplicadoRecienteAsync(
        Guid cotizacionId,
        IEnumerable<string> para,
        TimeSpan? ventana = null,
        CancellationToken cancellationToken = default);

    /// <summary>Correos de una cotización con sus destinatarios, del más reciente al más antiguo.</summary>
    Task<IReadOnlyList<clsCorreo>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);
}
