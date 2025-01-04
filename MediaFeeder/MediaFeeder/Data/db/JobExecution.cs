﻿using System.ComponentModel.DataAnnotations;

namespace MediaFeeder.Data.db;

public class JobExecution
{
    public JobExecution()
    {
        Jobmessages = new HashSet<JobMessage>();
    }

    public int Id { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    [MaxLength(250)] public string Description { get; set; }

    public int Status { get; set; }
    public int? UserId { get; set; }

    public virtual AuthUser User { get; set; }
    public virtual ICollection<JobMessage> Jobmessages { get; init; }
}